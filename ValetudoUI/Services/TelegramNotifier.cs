using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ValetudoUI.Components.Shared;
using ValetudoUI.Data;
using ValetudoUI.Options;

namespace ValetudoUI.Services;

/// <summary>
/// Sends Telegram messages when a run completes or the robot reports an error. Talks to the Bot API directly
/// (sendMessage) and never blocks the recorder or monitor: events are queued and sent on this service's loop.
/// </summary>
public sealed class TelegramNotifier(
    IHttpClientFactory httpClientFactory,
    IOptions<TelegramOptions> options,
    CleaningHistoryRecorder recorder,
    RobotMonitorService monitor,
    ILogger<TelegramNotifier> log) : BackgroundService
{
    public const string ClientName = "telegram";

    private readonly TelegramOptions _opts = options.Value;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private string? _lastErrorKey;

    public bool Enabled => _opts.Enabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            log.LogInformation("Telegram notifications disabled (set Telegram:BotToken and Telegram:ChatId to enable)");
            return;
        }

        log.LogInformation("Telegram notifications enabled for chat {ChatId} (runs: {Runs}, errors: {Errors})",
            _opts.ChatId, _opts.NotifyRunCompleted, _opts.NotifyErrors);

        recorder.RunCompleted += OnRunCompleted;
        monitor.StateChanged += OnState;
        try
        {
            await foreach (var text in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var (ok, error) = await SendAsync(text, stoppingToken);
                if (!ok)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    (ok, error) = await SendAsync(text, stoppingToken);
                }
                if (!ok) log.LogWarning("Telegram message dropped after retry: {Error}", error);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            recorder.RunCompleted -= OnRunCompleted;
            monitor.StateChanged -= OnState;
        }
    }

    // ---- event handlers (thread-pool threads; only enqueue) --------------------------

    private void OnRunCompleted(CleaningRun run)
    {
        if (_opts.NotifyRunCompleted) _queue.Writer.TryWrite(RunMessage(run));
    }

    private void OnState(RobotSnapshot s)
    {
        if (!_opts.NotifyErrors || s.Status is not { } status) return;
        if (status.Value != "error")
        {
            _lastErrorKey = null;
            return;
        }
        var key = $"{status.Value}|{status.Error?.Message}|{status.Error?.Subsystem}";
        if (key == _lastErrorKey) return;
        _lastErrorKey = key;
        _queue.Writer.TryWrite(ErrorMessage(status.Error));
    }

    // ---- message texts (Telegram HTML) -------------------------------------------------

    internal static string RunMessage(CleaningRun run)
    {
        var sb = new StringBuilder();
        sb.Append("🧹 <b>Cleaning finished</b>");
        sb.Append(" – ").Append(H(Format.RunType(run.RunType)));
        if (run.SegmentNames.Count > 0) sb.Append(": ").Append(H(string.Join(", ", run.SegmentNames)));
        sb.Append('\n');

        sb.Append("Duration ").Append(H(Format.Duration(run.DurationSeconds)));
        sb.Append(" · Area ").Append(H(Format.Area(run.AreaCm2)));
        if (run.EndStatus is not null) sb.Append(" · Ended ").Append(H(Format.Status(run.EndStatus).ToLowerInvariant()));
        sb.Append('\n');

        if (run.Phases.Count > 1)
        {
            sb.Append("Phases: ");
            sb.Append(H(string.Join(", ", run.Phases.Select(p =>
            {
                var meta = Format.Duration(p.DurationSeconds);
                if (p.AreaCm2 is > 0) meta += " · " + Format.Area(p.AreaCm2);
                return $"{p.Index + 1}. {Format.PhaseKind(p.Kind)} {meta}";
            }))));
            sb.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(run.ErrorMessage)) sb.Append("⚠️ ").Append(H(run.ErrorMessage)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    internal static string ErrorMessage(Valetudo.Models.StatusError? error)
    {
        var sb = new StringBuilder("⚠️ <b>Robot error</b>\n");
        sb.Append(H(string.IsNullOrWhiteSpace(error?.Message) ? "The robot reports an error." : error.Message));
        if (!string.IsNullOrWhiteSpace(error?.Subsystem)) sb.Append(" (").Append(H(error.Subsystem)).Append(')');
        return sb.ToString();
    }

    /// <summary>Telegram's HTML mode only needs these three escaped; leave "m²" and friends readable.</summary>
    private static string H(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---- sending ----------------------------------------------------------------------

    /// <summary>Sends a test message; returns null on success or the reason it failed.</summary>
    public async Task<string?> SendTestAsync(CancellationToken ct)
    {
        if (!_opts.Enabled) return "Telegram is not configured (Telegram:BotToken / Telegram:ChatId).";
        var (ok, error) = await SendAsync("✅ ValetudoUI can reach this chat.", ct);
        return ok ? null : error;
    }

    private async Task<(bool Ok, string? Error)> SendAsync(string text, CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(ClientName);
            // Serialise up front so the request carries a Content-Length instead of chunked encoding.
            var payload = JsonSerializer.Serialize(new
            {
                chat_id = _opts.ChatId,
                text,
                parse_mode = "HTML",
                disable_notification = _opts.Silent
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("sendMessage", content, ct);

            if (response.IsSuccessStatusCode)
            {
                log.LogDebug("Telegram message sent ({Length} chars)", text.Length);
                return (true, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var description = TryDescription(body) ?? body;
            var error = $"HTTP {(int)response.StatusCode}: {description}";
            log.LogWarning("Telegram sendMessage failed: {Error}", error);
            return (false, error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // Never log the exception object: the request URI (with the token) may be part of it.
            log.LogWarning("Telegram sendMessage failed: {Message}", e.Message);
            return (false, e.Message);
        }
    }

    private static string? TryDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
