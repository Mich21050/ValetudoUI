using System.Net.ServerSentEvents;
using System.Text.Json;
using ValetudoUI.Valetudo;
using ValetudoUI.Valetudo.Models;

namespace ValetudoUI.Services;

/// <summary>
/// The app's single connection to the robot: holds the latest state attributes and map,
/// keeps exactly two SSE streams open (attributes + map) and notifies subscribers.
/// Events are raised on thread-pool threads; subscribers must marshal to their own context.
/// </summary>
public sealed class RobotMonitorService(
    ValetudoClient client,
    IHostApplicationLifetime lifetime,
    ILogger<RobotMonitorService> log) : BackgroundService
{
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private RobotSnapshot _state = RobotSnapshot.Empty;
    private MapSnapshot? _map;
    private long _mapVersion;
    private int _attributesConnected;
    private int _mapConnected;
    private IReadOnlyList<StatisticEntry>? _currentStats;
    private string? _lastError;

    public RobotSnapshot State => Volatile.Read(ref _state);
    public MapSnapshot? Map => Volatile.Read(ref _map);
    public bool AttributesConnected => Volatile.Read(ref _attributesConnected) == 1;
    public bool MapConnected => Volatile.Read(ref _mapConnected) == 1;
    public bool Connected => AttributesConnected && MapConnected;
    public bool Initialised { get; private set; }
    public RobotInfo? Info { get; private set; }
    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>();
    public IReadOnlyList<StatisticEntry>? CurrentStats => Volatile.Read(ref _currentStats);
    public string? LastError => Volatile.Read(ref _lastError);

    public event Action<RobotSnapshot>? StateChanged;
    public event Action<MapSnapshot>? MapChanged;
    public event Action? ConnectionChanged;

    public bool HasCapability(string name) => Capabilities.Contains(name);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(stoppingToken);
            await InitialFetchAsync(stoppingToken);
            await Task.WhenAll(
                RunSseLoopAsync("api/v2/robot/state/attributes/sse", "StateAttributesUpdated", HandleAttributesEvent,
                    RefreshAttributesAsync, v => Volatile.Write(ref _attributesConnected, v), stoppingToken),
                RunSseLoopAsync("api/v2/robot/state/map/sse", "MapUpdated", HandleMapEvent,
                    RefreshMapAsync, v => Volatile.Write(ref _mapConnected, v), stoppingToken),
                RunStatisticsLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Re-fetch attributes and map over REST (used on demand, e.g. after commands).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await RefreshAttributesAsync(ct);
        await RefreshMapAsync(ct);
    }

    public async Task RefreshCurrentStatisticsAsync(CancellationToken ct)
    {
        if (!HasCapability(Valetudo.Models.Capabilities.CurrentStatistics)) return;
        var stats = await client.GetCurrentStatisticsAsync(ct);
        Volatile.Write(ref _currentStats, stats);
        Raise(StateChanged, State);
    }

    // ---- startup ------------------------------------------------------------

    private async Task WaitForApplicationStartedAsync(CancellationToken ct)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = lifetime.ApplicationStarted.Register(() => tcs.TrySetResult());
        await tcs.Task.WaitAsync(ct);
    }

    private async Task InitialFetchAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Info = await client.GetRobotInfoAsync(ct);
                Capabilities = await client.GetCapabilitiesAsync(ct);
                await RefreshAsync(ct);
                Initialised = true;
                Volatile.Write(ref _lastError, null);
                log.LogInformation("Connected to {Manufacturer} {Model} ({Impl}) with {Count} capabilities",
                    Info.Manufacturer, Info.ModelName, Info.Implementation, Capabilities.Count);
                Raise(ConnectionChanged);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                Volatile.Write(ref _lastError, e.Message);
                log.LogWarning("Initial fetch from robot failed: {Message}. Retrying in {Delay}s", e.Message, delay.TotalSeconds);
                Raise(ConnectionChanged);
                await Task.Delay(delay, ct);
                delay = Min(delay * 2, MaxBackoff);
            }
        }
    }

    // ---- SSE ----------------------------------------------------------------

    private async Task RunSseLoopAsync(
        string path, string eventName, Action<string> handler, Func<CancellationToken, Task> refresh,
        Action<int> setConnected, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await client.OpenSseAsync(path, ct);
                var tracked = new ActivityTrackingStream(connection.Stream);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var watchdog = WatchdogAsync(tracked, linked, path);

                setConnected(1);
                Volatile.Write(ref _lastError, null);
                Raise(ConnectionChanged);
                delay = TimeSpan.FromSeconds(1);
                log.LogInformation("SSE connected: {Path}", path);

                // SSE sends no initial snapshot; catch up over REST (also covers transitions missed while disconnected).
                try { await refresh(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Refresh after SSE connect failed: {Message}", e.Message); }

                await foreach (var item in SseParser.Create(tracked).EnumerateAsync(linked.Token))
                {
                    if (item.EventType != eventName) continue;
                    try { handler(item.Data); }
                    catch (Exception e) { log.LogWarning(e, "Failed to handle {Event} event", eventName); }
                }

                linked.Cancel();
                try { await watchdog; } catch (OperationCanceledException) { }
                log.LogInformation("SSE stream ended: {Path}", path);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Volatile.Write(ref _lastError, e.Message);
                log.LogWarning("SSE {Path} failed: {Message}. Reconnecting in {Delay}s", path, e.Message, delay.TotalSeconds);
            }
            finally
            {
                setConnected(0);
                Raise(ConnectionChanged);
            }

            if (ct.IsCancellationRequested) break;
            await Task.Delay(delay, ct);
            delay = Min(delay * 2, MaxBackoff);
        }
    }

    private async Task WatchdogAsync(ActivityTrackingStream stream, CancellationTokenSource cts, string path)
    {
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            if (stream.MillisecondsSinceActivity > SilenceTimeout.TotalMilliseconds)
            {
                log.LogWarning("SSE {Path} silent for {Seconds}s, reconnecting", path, SilenceTimeout.TotalSeconds);
                cts.Cancel();
            }
        }
    }

    private void HandleAttributesEvent(string data)
    {
        var attrs = JsonSerializer.Deserialize<List<StateAttribute>>(data, ValetudoJson.Options) ?? [];
        SetState(attrs);
    }

    private void HandleMapEvent(string json) => SetMap(json);

    private async Task RefreshAttributesAsync(CancellationToken ct)
        => SetState(await client.GetStateAttributesAsync(ct));

    private async Task RefreshMapAsync(CancellationToken ct)
        => SetMap(await client.GetMapJsonAsync(ct));

    private void SetState(IReadOnlyList<StateAttribute> attributes)
    {
        var snapshot = new RobotSnapshot(attributes, DateTime.UtcNow);
        Volatile.Write(ref _state, snapshot);
        Raise(StateChanged, snapshot);
    }

    private void SetMap(string json)
    {
        var summary = MapSummaryParser.Parse(json);
        var snapshot = new MapSnapshot(json, summary.Nonce, Interlocked.Increment(ref _mapVersion), summary, DateTime.UtcNow);
        Volatile.Write(ref _map, snapshot);
        Raise(MapChanged, snapshot);
    }

    // ---- current statistics while a task is running -------------------------

    private async Task RunStatisticsLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (!State.IsInCleaningTask) continue;
            try { await RefreshCurrentStatisticsAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { log.LogDebug("Current statistics poll failed: {Message}", e.Message); }
        }
    }

    // ---- events -------------------------------------------------------------

    private void Raise<T>(Action<T>? handlers, T arg)
    {
        if (handlers is null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { ((Action<T>)d)(arg); }
            catch (Exception e) { log.LogWarning(e, "Event subscriber threw"); }
        }
    }

    private void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception e) { log.LogWarning(e, "Event subscriber threw"); }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
