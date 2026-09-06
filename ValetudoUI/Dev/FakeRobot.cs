using System.Text.Json;
using System.Threading.Channels;

namespace ValetudoUI.Dev;

/// <summary>
/// Development-only stand-in for Valetudo: serves a fixture map (Fixtures/map.json) or a synthetic one,
/// mimics the state attributes/SSE wire format and plays scripted cleaning runs so the history recorder
/// can be exercised without running the vacuum.
/// </summary>
public sealed class FakeRobot(IWebHostEnvironment env, IConfiguration configuration, ILogger<FakeRobot> log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _lock = new();
    private readonly List<Channel<(string Event, string Data)>> _attributeSubscribers = [];
    private readonly List<Channel<(string Event, string Data)>> _mapSubscribers = [];

    private JsonElement _baseMap = LoadOrGenerateMap(env);
    private readonly List<(int X, int Y)> _floorPixels = [];
    private int _pixelSize = 5;
    private (int X, int Y) _charger;
    private (int X, int Y) _robot;
    private double _angle;
    private readonly List<int> _path = [];
    private string _nonce = Guid.NewGuid().ToString();
    private HashSet<string> _activeSegments = [];
    private CancellationTokenSource? _scenario;

    public string Status { get; private set; } = "docked";
    public string Flag { get; private set; } = "none";
    public string? ErrorMessage { get; private set; }
    public bool MopAttached { get; private set; } = true;
    public int Battery { get; private set; } = 100;
    public string DockStatus { get; private set; } = "idle";
    public Dictionary<string, string> Presets { get; } = new()
    {
        ["fan_speed"] = "medium",
        ["water_grade"] = "medium",
        ["operation_mode"] = "vacuum_and_mop"
    };
    public Dictionary<string, double> Consumables { get; } = new()
    {
        ["brush/main"] = 12000,
        ["brush/side_right"] = 8000,
        ["filter/main"] = 5000,
        ["cleaning/sensor"] = 1200,
        ["cleaning/wheel"] = 3000
    };
    public DateTime? RunStartedUtc { get; private set; }
    public int LastRunSeconds { get; private set; }
    public long LastRunAreaCm2 { get; private set; }
    public int TotalRuns { get; private set; } = 12;
    public int TotalSeconds { get; private set; } = 12 * 1800;
    public long TotalAreaCm2 { get; private set; } = 12 * 350000L;

    public static readonly string[] CapabilityList =
    [
        "BasicControlCapability", "FanSpeedControlCapability", "LocateCapability", "MapSegmentEditCapability",
        "MapSegmentRenameCapability", "MapResetCapability", "CombinedVirtualRestrictionsCapability",
        "SpeakerVolumeControlCapability", "SpeakerTestCapability", "PendingMapChangeHandlingCapability",
        "TotalStatisticsCapability", "CurrentStatisticsCapability", "VoicePackManagementCapability",
        "HighResolutionManualControlCapability", "DoNotDisturbCapability", "MappingPassCapability",
        "MapSegmentationCapability", "WaterUsageControlCapability", "ZoneCleaningCapability",
        "ConsumableMonitoringCapability", "OperationModeControlCapability", "CarpetSensorModeControlCapability",
        "ObstacleImagesCapability", "MapSegmentMaterialControlCapability", "CarpetModeControlCapability",
        "KeyLockCapability", "AutoEmptyDockManualTriggerCapability", "MopDockCleanManualTriggerCapability",
        "MopDockDryManualTriggerCapability", "GoToLocationCapability", "ObstacleAvoidanceControlCapability",
        "PetObstacleAvoidanceControlCapability", "MopExtensionControlCapability", "QuirksCapability"
    ];

    public void Initialise()
    {
        lock (_lock)
        {
            _floorPixels.Clear();
            var root = _baseMap;
            _pixelSize = root.TryGetProperty("pixelSize", out var ps) ? ps.GetInt32() : 5;
            foreach (var layer in root.GetProperty("layers").EnumerateArray())
            {
                var type = layer.GetProperty("type").GetString();
                if (type is not ("floor" or "segment")) continue;
                var c = layer.GetProperty("compressedPixels").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                for (var i = 0; i + 2 < c.Length; i += 3)
                    for (var k = 0; k < c[i + 2]; k++)
                        _floorPixels.Add((c[i] + k, c[i + 1]));
            }
            foreach (var e in root.GetProperty("entities").EnumerateArray())
            {
                if (e.GetProperty("type").GetString() == "charger_location")
                {
                    var p = e.GetProperty("points");
                    _charger = (p[0].GetInt32(), p[1].GetInt32());
                }
            }
            if (_charger == default && _floorPixels.Count > 0)
                _charger = (_floorPixels[0].X * _pixelSize, _floorPixels[0].Y * _pixelSize);
            _robot = _charger;
            _angle = 0;
        }
        log.LogInformation("Fake robot ready with {Pixels} floor pixels, charger at {Charger}", _floorPixels.Count, _charger);

        // Dev hook: start cleaning immediately (tests recorder recovery after an app restart).
        if (configuration.GetValue<int?>("Valetudo:FakeStartScenarioSeconds") is { } seconds and > 0)
            StartScenario(seconds, null, ["1"], "segment", null, false);
    }

    // ---- subscriptions ----------------------------------------------------------

    public Channel<(string Event, string Data)> Subscribe(bool map)
    {
        var ch = Channel.CreateUnbounded<(string, string)>();
        lock (_lock) (map ? _mapSubscribers : _attributeSubscribers).Add(ch);
        return ch;
    }

    public void Unsubscribe(Channel<(string Event, string Data)> ch)
    {
        lock (_lock)
        {
            _mapSubscribers.Remove(ch);
            _attributeSubscribers.Remove(ch);
        }
    }

    private void Publish(bool map, string ev, string data)
    {
        List<Channel<(string, string)>> targets;
        lock (_lock) targets = (map ? _mapSubscribers : _attributeSubscribers).ToList();
        foreach (var t in targets) t.Writer.TryWrite((ev, data));
    }

    private void PublishState() => Publish(false, "StateAttributesUpdated", AttributesJson());
    private void PublishMap() => Publish(true, "MapUpdated", MapJson());

    // ---- JSON views -------------------------------------------------------------

    public string AttributesJson()
    {
        lock (_lock)
        {
            var list = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["__class"] = "StatusStateAttribute",
                    ["metaData"] = new { },
                    ["value"] = Status,
                    ["flag"] = Flag,
                    ["error"] = Status == "error" ? new { severity = new { kind = "transient", level = "error" }, subsystem = "navigation", message = ErrorMessage ?? "Fake error", vendorErrorCode = 42 } : null
                },
                new { __class = "BatteryStateAttribute", metaData = new { }, level = Battery, flag = Status == "docked" ? (Battery >= 100 ? "charged" : "charging") : "discharging" },
                new { __class = "AttachmentStateAttribute", metaData = new { }, type = "mop", attached = MopAttached },
                new { __class = "DockStatusStateAttribute", metaData = new { }, value = DockStatus },
                new { __class = "DockComponentStateAttribute", metaData = new { }, type = "dustbag", value = "ok" },
                new { __class = "DockComponentStateAttribute", metaData = new { }, type = "water_tank_clean", value = "ok" },
                new { __class = "SomethingNewStateAttribute", metaData = new { }, value = "unknown attributes must be tolerated" }
            };
            foreach (var (type, value) in Presets)
                list.Add(new { __class = "PresetSelectionStateAttribute", metaData = new { }, type, value });
            return JsonSerializer.Serialize(list, Json);
        }
    }

    public string MapJson()
    {
        lock (_lock)
        {
            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_baseMap.GetRawText())!;
            var output = new Dictionary<string, object?>();
            foreach (var (k, v) in root)
            {
                switch (k)
                {
                    case "metaData":
                        var md = JsonSerializer.Deserialize<Dictionary<string, object?>>(v.GetRawText()) ?? [];
                        md["nonce"] = _nonce;
                        output[k] = md;
                        break;
                    case "layers":
                        var layers = new List<object?>();
                        foreach (var layer in v.EnumerateArray())
                        {
                            var l = JsonSerializer.Deserialize<Dictionary<string, object?>>(layer.GetRawText())!;
                            if (layer.GetProperty("type").GetString() == "segment")
                            {
                                var m = JsonSerializer.Deserialize<Dictionary<string, object?>>(layer.GetProperty("metaData").GetRawText())!;
                                var id = layer.GetProperty("metaData").GetProperty("segmentId");
                                var sid = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
                                m["active"] = _activeSegments.Contains(sid);
                                l["metaData"] = m;
                            }
                            layers.Add(l);
                        }
                        output[k] = layers;
                        break;
                    case "entities":
                        var entities = new List<object?>();
                        foreach (var e in v.EnumerateArray())
                        {
                            var type = e.GetProperty("type").GetString();
                            if (type is "robot_position" or "path" or "predicted_path") continue;
                            entities.Add(JsonSerializer.Deserialize<object>(e.GetRawText()));
                        }
                        entities.Add(new { __class = "PointMapEntity", metaData = new { angle = _angle }, points = new[] { _robot.X, _robot.Y }, type = "robot_position" });
                        if (_path.Count >= 2)
                            entities.Add(new { __class = "PathMapEntity", metaData = new { }, points = _path.ToArray(), type = "path" });
                        output[k] = entities;
                        break;
                    default:
                        output[k] = JsonSerializer.Deserialize<object>(v.GetRawText());
                        break;
                }
            }
            return JsonSerializer.Serialize(output, Json);
        }
    }

    public string SegmentsJson()
    {
        lock (_lock)
        {
            var list = new List<object>();
            foreach (var layer in _baseMap.GetProperty("layers").EnumerateArray())
            {
                if (layer.GetProperty("type").GetString() != "segment") continue;
                var m = layer.GetProperty("metaData");
                var id = m.GetProperty("segmentId");
                list.Add(new
                {
                    __class = "ValetudoMapSegment",
                    metaData = new { },
                    id = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText(),
                    name = m.TryGetProperty("name", out var n) ? n.GetString() : null
                });
            }
            return JsonSerializer.Serialize(list, Json);
        }
    }

    public object CurrentStatistics()
    {
        lock (_lock)
        {
            var seconds = RunStartedUtc is { } s ? (int)(DateTime.UtcNow - s).TotalSeconds : LastRunSeconds;
            var area = RunStartedUtc is not null ? _path.Count / 2 * 25L * 5 : LastRunAreaCm2;
            return new object[]
            {
                new { __class = "ValetudoDataPoint", metaData = new { }, timestamp = DateTime.UtcNow, type = "time", value = seconds },
                new { __class = "ValetudoDataPoint", metaData = new { }, timestamp = DateTime.UtcNow, type = "area", value = area }
            };
        }
    }

    public object TotalStatistics() => new object[]
    {
        new { __class = "ValetudoDataPoint", metaData = new { }, timestamp = DateTime.UtcNow, type = "time", value = TotalSeconds },
        new { __class = "ValetudoDataPoint", metaData = new { }, timestamp = DateTime.UtcNow, type = "area", value = TotalAreaCm2 },
        new { __class = "ValetudoDataPoint", metaData = new { }, timestamp = DateTime.UtcNow, type = "count", value = TotalRuns }
    };

    public object ConsumablesJson() => Consumables.Select(kv =>
    {
        var parts = kv.Key.Split('/');
        return new { __class = "ValetudoConsumable", metaData = new { }, type = parts[0], subType = parts[1], remaining = new { value = kv.Value, unit = "minutes" } };
    }).ToList();

    // ---- commands -----------------------------------------------------------------

    public void SetPreset(string type, string value)
    {
        lock (_lock) Presets[type] = value;
        PublishState();
    }

    public void ResetConsumable(string type, string subType)
    {
        lock (_lock) Consumables[$"{type}/{subType}"] = 15000;
    }

    public void SetDockStatus(string status)
    {
        lock (_lock) DockStatus = status;
        PublishState();
        if (status != "idle")
            _ = Task.Delay(8000).ContinueWith(_ => SetDockStatus("idle"));
    }

    public void GoTo()
    {
        lock (_lock) { Status = "moving"; Flag = "target"; }
        PublishState();
        _ = Task.Delay(5000).ContinueWith(_ =>
        {
            lock (_lock) { Status = "idle"; Flag = "none"; }
            PublishState();
        });
    }

    public void Pause()
    {
        lock (_lock) { if (Status == "cleaning") { Status = "paused"; } }
        PublishState();
    }

    public void Resume()
    {
        lock (_lock) { if (Status == "paused") Status = "cleaning"; }
        PublishState();
    }

    public void Stop()
    {
        CancelScenario();
        lock (_lock) { Status = "idle"; Flag = "resumable"; }
        PublishState();
    }

    public void Home()
    {
        CancelScenario();
        _ = ReturnHomeAsync(CancellationToken.None);
    }

    public void RaiseError(string message)
    {
        CancelScenario();
        lock (_lock) { Status = "error"; Flag = "none"; ErrorMessage = message; }
        PublishState();
    }

    /// <summary>
    /// Starts a scripted run: cleaning for <paramref name="seconds"/>, optional path reset at <paramref name="pathResetAt"/> seconds.
    /// With <paramref name="washAt"/> the robot drives to the dock at that second, reports docked+resumable for a few
    /// seconds (mop wash) and continues; <paramref name="thenMop"/> additionally simulates "vacuum then mop" with pad
    /// removal: mops detached in the first part, attached after the dock visit.
    /// </summary>
    public void StartScenario(int seconds, int? pathResetAt, IReadOnlyList<string>? segmentIds, string flag, int? washAt = null, bool thenMop = false)
    {
        CancelScenario();
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _scenario = cts;
            _activeSegments = segmentIds is null ? [] : segmentIds.ToHashSet();
            Status = "cleaning";
            Flag = flag;
            ErrorMessage = null;
            RunStartedUtc = DateTime.UtcNow;
            if (thenMop)
            {
                Presets["operation_mode"] = "vacuum_then_mop";
                MopAttached = false;
            }
            _path.Clear();
            _path.Add(_robot.X);
            _path.Add(_robot.Y);
        }
        PublishState();
        PublishMap();
        _ = RunScenarioAsync(seconds, pathResetAt, washAt, thenMop, cts.Token);
        log.LogInformation("Fake scenario started: {Seconds}s, path reset at {Reset}, wash at {Wash}, then-mop {ThenMop}", seconds, pathResetAt, washAt, thenMop);
    }

    private void CancelScenario()
    {
        CancellationTokenSource? old;
        lock (_lock) { old = _scenario; _scenario = null; }
        old?.Cancel();
    }

    private async Task RunScenarioAsync(int seconds, int? pathResetAt, int? washAt, bool thenMop, CancellationToken ct)
    {
        var rng = new Random();
        var started = DateTime.UtcNow;
        var resetDone = false;
        var washDone = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (elapsed >= seconds) break;

                if (washAt is { } w && !washDone && elapsed >= w)
                {
                    washDone = true;
                    await MidRunDockVisitAsync(thenMop, ct);
                    continue;
                }

                lock (_lock)
                {
                    if (Status != "cleaning") continue;
                    if (pathResetAt is { } r && !resetDone && elapsed >= r)
                    {
                        _path.Clear();
                        resetDone = true;
                    }
                    RandomStep(rng);
                    _path.Add(_robot.X);
                    _path.Add(_robot.Y);
                    if (rng.Next(10) == 0 && Battery > 5) Battery--;
                }
                PublishMap();
            }
            if (!ct.IsCancellationRequested) await ReturnHomeAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Drives to the dock, reports docked + resumable (washing mops) for a while, then resumes with a fresh path.</summary>
    private async Task MidRunDockVisitAsync(bool thenMop, CancellationToken ct)
    {
        await DriveToDockAsync(ct);
        lock (_lock)
        {
            Status = "docked";
            Flag = "resumable";
            DockStatus = "cleaning";
        }
        PublishState();
        await Task.Delay(8000, ct);
        lock (_lock)
        {
            if (thenMop) MopAttached = true;
            DockStatus = "idle";
            Status = "cleaning";
            Flag = _activeSegments.Count > 0 ? "segment" : "none";
            _path.Clear();
            _path.Add(_robot.X);
            _path.Add(_robot.Y);
        }
        PublishState();
        PublishMap();
        log.LogInformation("Fake robot resumed after dock visit (mop attached: {Mop})", MopAttached);
    }

    private async Task DriveToDockAsync(CancellationToken ct)
    {
        lock (_lock) { Status = "returning"; }
        PublishState();
        for (var i = 1; i <= 4; i++)
        {
            await Task.Delay(1500, ct);
            lock (_lock)
            {
                _robot = (_robot.X + (_charger.X - _robot.X) * i / 4, _robot.Y + (_charger.Y - _robot.Y) * i / 4);
                _path.Add(_robot.X);
                _path.Add(_robot.Y);
            }
            PublishMap();
        }
    }

    private async Task ReturnHomeAsync(CancellationToken ct)
    {
        await DriveToDockAsync(ct);
        lock (_lock)
        {
            Status = "docked";
            Flag = "none";
            _activeSegments = [];
            if (RunStartedUtc is { } s)
            {
                LastRunSeconds = (int)(DateTime.UtcNow - s).TotalSeconds;
                LastRunAreaCm2 = _path.Count / 2 * 25L * 5;
                TotalRuns++;
                TotalSeconds += LastRunSeconds;
                TotalAreaCm2 += LastRunAreaCm2;
                RunStartedUtc = null;
            }
        }
        PublishState();
        PublishMap();
    }

    private void RandomStep(Random rng)
    {
        // Random walk over floor pixels, biased to keep the current heading.
        var px = _robot.X / _pixelSize;
        var py = _robot.Y / _pixelSize;
        var candidates = new List<(int X, int Y, double Angle)>();
        foreach (var (dx, dy, a) in new[] { (1, 0, 90.0), (-1, 0, 270.0), (0, 1, 180.0), (0, -1, 0.0) })
        {
            var nx = px + dx * 3;
            var ny = py + dy * 3;
            if (_floorPixels.Contains((nx, ny))) candidates.Add((nx, ny, a));
        }
        if (candidates.Count == 0) return;
        var keep = candidates.FirstOrDefault(c => Math.Abs(c.Angle - _angle) < 1);
        var pick = keep != default && rng.Next(4) != 0 ? keep : candidates[rng.Next(candidates.Count)];
        _robot = (pick.X * _pixelSize, pick.Y * _pixelSize);
        _angle = pick.Angle;
    }

    // ---- map fixture -----------------------------------------------------------------

    private static JsonElement LoadOrGenerateMap(IWebHostEnvironment env)
    {
        var fixture = Path.Combine(env.ContentRootPath, "Fixtures", "map.json");
        if (File.Exists(fixture))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
            return doc.RootElement.Clone();
        }
        using var generated = JsonDocument.Parse(JsonSerializer.Serialize(GenerateMap(), Json));
        return generated.RootElement.Clone();
    }

    /// <summary>A synthetic 3-room flat: 150x100 px at 5 cm/px starting at pixel (100,100).</summary>
    private static object GenerateMap()
    {
        const int x0 = 100, y0 = 100, w = 150, h = 100;
        var rooms = new[]
        {
            (Id: "1", Name: "Living room", X: x0, W: 70, Y: y0, H: h),
            (Id: "2", Name: "Kitchen", X: x0 + 70, W: 40, Y: y0, H: 55),
            (Id: "3", Name: "Bedroom", X: x0 + 70, W: 80, Y: y0 + 55, H: 45),
            (Id: "4", Name: "Bath", X: x0 + 110, W: 40, Y: y0, H: 55)
        };

        var layers = new List<object>();
        foreach (var r in rooms)
        {
            var runs = new List<int>();
            for (var y = r.Y + 1; y < r.Y + r.H - 1; y++) { runs.Add(r.X + 1); runs.Add(y); runs.Add(r.W - 2); }
            layers.Add(Layer("segment", runs, new Dictionary<string, object?> { ["segmentId"] = r.Id, ["name"] = r.Name, ["active"] = false, ["area"] = (r.W - 2) * (r.H - 2) * 25, ["source"] = "regular" }));
        }
        var walls = new List<int>();
        foreach (var r in rooms)
        {
            walls.Add(r.X); walls.Add(r.Y); walls.Add(r.W);
            walls.Add(r.X); walls.Add(r.Y + r.H - 1); walls.Add(r.W);
            for (var y = r.Y + 1; y < r.Y + r.H - 1; y++)
            {
                walls.Add(r.X); walls.Add(y); walls.Add(1);
                walls.Add(r.X + r.W - 1); walls.Add(y); walls.Add(1);
            }
        }
        layers.Add(Layer("wall", walls, new Dictionary<string, object?> { ["area"] = walls.Count / 3 * 25 }));

        var charger = new[] { (x0 + 5) * 5, (y0 + 5) * 5 };
        return new
        {
            __class = "ValetudoMap",
            metaData = new { version = 2, nonce = Guid.NewGuid().ToString(), totalLayerArea = w * h * 25 },
            size = new { x = 6554, y = 6554 },
            pixelSize = 5,
            layers,
            entities = new object[]
            {
                new { __class = "PointMapEntity", metaData = new { }, points = charger, type = "charger_location" },
                new { __class = "PointMapEntity", metaData = new { angle = 0 }, points = charger, type = "robot_position" },
                new { __class = "PolygonMapEntity", metaData = new { }, points = new[] { (x0 + 75) * 5, (y0 + 5) * 5, (x0 + 95) * 5, (y0 + 5) * 5, (x0 + 95) * 5, (y0 + 20) * 5, (x0 + 75) * 5, (y0 + 20) * 5 }, type = "no_go_area" },
                new { __class = "LineMapEntity", metaData = new { }, points = new[] { (x0 + 70) * 5, (y0 + 80) * 5, (x0 + 70) * 5, (y0 + 98) * 5 }, type = "virtual_wall" }
            }
        };
    }

    private static object Layer(string type, List<int> compressed, Dictionary<string, object?> metaData)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue, count = 0;
        long sumX = 0, sumY = 0;
        for (var i = 0; i + 2 < compressed.Count; i += 3)
        {
            var x = compressed[i]; var y = compressed[i + 1]; var n = compressed[i + 2];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x + n - 1);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            count += n; sumX += (long)n * (2 * x + n - 1) / 2; sumY += (long)n * y;
        }
        if (count == 0) { minX = maxX = minY = maxY = 0; }
        return new
        {
            __class = "MapLayer",
            metaData,
            type,
            pixels = Array.Empty<int>(),
            compressedPixels = compressed,
            dimensions = new
            {
                x = new { min = minX, max = maxX, mid = (minX + maxX) / 2, avg = count > 0 ? (int)(sumX / count) : 0 },
                y = new { min = minY, max = maxY, mid = (minY + maxY) / 2, avg = count > 0 ? (int)(sumY / count) : 0 },
                pixelCount = count
            }
        };
    }
}
