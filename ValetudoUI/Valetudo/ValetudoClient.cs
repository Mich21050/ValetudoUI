using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ValetudoUI.Valetudo.Models;

namespace ValetudoUI.Valetudo;

/// <summary>Thin REST wrapper over Valetudo's /api/v2. Singleton; resolves named HttpClients per call.</summary>
public sealed class SseConnection(HttpClient client, HttpResponseMessage response, Stream stream) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        response.Dispose();
        GC.KeepAlive(client);
    }
}

public sealed class ValetudoClient(IHttpClientFactory factory)
{
    public const string RestClientName = "valetudo";
    public const string SseClientName = "valetudo-sse";

    private HttpClient Rest => factory.CreateClient(RestClientName);

    // ---- robot / state ----------------------------------------------------

    public Task<RobotInfo> GetRobotInfoAsync(CancellationToken ct) => GetAsync<RobotInfo>("api/v2/robot", ct);

    public Task<HashSet<string>> GetCapabilitiesAsync(CancellationToken ct) => GetAsync<HashSet<string>>("api/v2/robot/capabilities", ct);

    public async Task<IReadOnlyList<StateAttribute>> GetStateAttributesAsync(CancellationToken ct)
        => await GetAsync<List<StateAttribute>>("api/v2/robot/state/attributes", ct);

    public async Task<string> GetMapJsonAsync(CancellationToken ct)
    {
        using var resp = await Rest.GetAsync("api/v2/robot/state/map", ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Opens an SSE stream. Dispose the connection when done; read <see cref="SseConnection.Stream"/> with SseParser.</summary>
    public async Task<SseConnection> OpenSseAsync(string relativePath, CancellationToken ct)
    {
        // The HttpClient must stay referenced while the stream is open, otherwise IHttpClientFactory may
        // dispose the expired handler underneath us and the connection gets reset.
        var client = factory.CreateClient(SseClientName);
        var req = new HttpRequestMessage(HttpMethod.Get, relativePath);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            throw new ValetudoApiException($"SSE {relativePath} returned HTTP {code}", code);
        }
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return new SseConnection(client, resp, stream);
    }

    // ---- basic control ----------------------------------------------------

    public Task BasicControlAsync(BasicControlAction action, CancellationToken ct)
        => PutCapabilityAsync(Capabilities.BasicControl, new { action = action.ToString().ToLowerInvariant() }, ct);

    public Task LocateAsync(CancellationToken ct) => PutCapabilityAsync(Capabilities.Locate, new { action = "locate" }, ct);

    // ---- segments -----------------------------------------------------------

    public async Task<IReadOnlyList<SegmentInfo>> GetSegmentsAsync(CancellationToken ct)
        => await GetAsync<List<SegmentInfo>>($"api/v2/robot/capabilities/{Capabilities.MapSegmentation}", ct);

    public Task<SegmentationProperties> GetSegmentationPropertiesAsync(CancellationToken ct)
        => GetAsync<SegmentationProperties>($"api/v2/robot/capabilities/{Capabilities.MapSegmentation}/properties", ct);

    public Task CleanSegmentsAsync(IReadOnlyList<string> segmentIds, int iterations, bool customOrder, CancellationToken ct)
        => PutCapabilityAsync(Capabilities.MapSegmentation, new Dictionary<string, object?>
        {
            ["action"] = "start_segment_action",
            ["segment_ids"] = segmentIds,
            ["iterations"] = iterations,
            ["customOrder"] = customOrder
        }, ct);

    // ---- presets ------------------------------------------------------------

    public async Task<IReadOnlyList<string>> GetPresetsAsync(PresetCapability cap, CancellationToken ct)
        => await GetAsync<List<string>>($"api/v2/robot/capabilities/{cap.CapabilityName()}/presets", ct);

    public Task SetPresetAsync(PresetCapability cap, string name, CancellationToken ct)
        => PutAsync($"api/v2/robot/capabilities/{cap.CapabilityName()}/preset", new { name }, ct);

    // ---- zones / go-to ------------------------------------------------------

    public Task<ZoneProperties> GetZonePropertiesAsync(CancellationToken ct)
        => GetAsync<ZoneProperties>($"api/v2/robot/capabilities/{Capabilities.ZoneCleaning}/properties", ct);

    public Task CleanZonesAsync(IReadOnlyList<ZoneRect> zones, int iterations, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["action"] = "clean",
            ["zones"] = zones.Select(z =>
            {
                int x1 = Math.Min(z.X1, z.X2), x2 = Math.Max(z.X1, z.X2);
                int y1 = Math.Min(z.Y1, z.Y2), y2 = Math.Max(z.Y1, z.Y2);
                return new
                {
                    points = new
                    {
                        pA = new { x = x1, y = y1 },
                        pB = new { x = x2, y = y1 },
                        pC = new { x = x2, y = y2 },
                        pD = new { x = x1, y = y2 }
                    }
                };
            }).ToList(),
            ["iterations"] = iterations
        };
        return PutCapabilityAsync(Capabilities.ZoneCleaning, body, ct);
    }

    public Task GoToAsync(int x, int y, CancellationToken ct)
        => PutCapabilityAsync(Capabilities.GoToLocation, new { action = "goto", coordinates = new { x, y } }, ct);

    // ---- consumables / statistics ------------------------------------------

    public async Task<IReadOnlyList<Consumable>> GetConsumablesAsync(CancellationToken ct)
        => await GetAsync<List<Consumable>>($"api/v2/robot/capabilities/{Capabilities.ConsumableMonitoring}", ct);

    public Task ResetConsumableAsync(string type, string subType, CancellationToken ct)
        => PutAsync($"api/v2/robot/capabilities/{Capabilities.ConsumableMonitoring}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(subType)}",
            new { action = "reset" }, ct);

    public async Task<IReadOnlyList<StatisticEntry>> GetCurrentStatisticsAsync(CancellationToken ct)
        => await GetAsync<List<StatisticEntry>>($"api/v2/robot/capabilities/{Capabilities.CurrentStatistics}", ct);

    public async Task<IReadOnlyList<StatisticEntry>> GetTotalStatisticsAsync(CancellationToken ct)
        => await GetAsync<List<StatisticEntry>>($"api/v2/robot/capabilities/{Capabilities.TotalStatistics}", ct);

    // ---- dock ---------------------------------------------------------------

    public Task TriggerAutoEmptyAsync(CancellationToken ct)
        => PutCapabilityAsync(Capabilities.AutoEmptyDockManualTrigger, new { action = "trigger" }, ct);

    public Task MopDockCleanAsync(bool start, CancellationToken ct)
        => PutCapabilityAsync(Capabilities.MopDockCleanManualTrigger, new { action = start ? "start" : "stop" }, ct);

    public Task MopDockDryAsync(bool start, CancellationToken ct)
        => PutCapabilityAsync(Capabilities.MopDockDryManualTrigger, new { action = start ? "start" : "stop" }, ct);

    // ---- plumbing -----------------------------------------------------------

    private Task PutCapabilityAsync(string capability, object body, CancellationToken ct)
        => PutAsync($"api/v2/robot/capabilities/{capability}", body, ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var resp = await Rest.GetAsync(path, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<T>(ValetudoJson.Options, ct)
               ?? throw new ValetudoApiException("Empty response from robot", (int)resp.StatusCode);
    }

    private async Task PutAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await Rest.PutAsJsonAsync(path, body, ValetudoJson.Options, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var message = body.Trim();
        if (message.StartsWith('"'))
        {
            try { message = JsonSerializer.Deserialize<string>(message) ?? message; } catch (JsonException) { }
        }
        if (string.IsNullOrWhiteSpace(message)) message = $"Robot returned HTTP {(int)resp.StatusCode}";
        throw new ValetudoApiException(message, (int)resp.StatusCode);
    }
}
