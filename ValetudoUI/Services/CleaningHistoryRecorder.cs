using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValetudoUI.Data;
using ValetudoUI.Options;
using ValetudoUI.Valetudo;

namespace ValetudoUI.Services;

public abstract record RecorderHint;
public sealed record SegmentCleanHint(IReadOnlyList<string> SegmentIds) : RecorderHint;
public sealed record ZoneCleanHint : RecorderHint;
public sealed record FullCleanHint : RecorderHint;

/// <summary>
/// Derives cleaning runs from the robot's status transitions (Valetudo has no history and no "finished" event),
/// accumulates the path across map updates and stores each run with its coverage map in SQLite.
/// Runs on its own thread; the monitor's events are only enqueued.
/// </summary>
public sealed class CleaningHistoryRecorder(
    RobotMonitorService monitor,
    ValetudoClient client,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<HistoryOptions> options,
    ILogger<CleaningHistoryRecorder> log) : BackgroundService
{
    private static readonly TimeSpan AdoptWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HintWindow = TimeSpan.FromMinutes(2);

    private readonly Channel<RobotEvent> _events = Channel.CreateBounded<RobotEvent>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly HistoryOptions _opts = options.Value;

    private CleaningRun? _run;
    private PathAccumulator _path = new();
    private string? _latestMapJson;
    private DateTime? _graceDeadline;
    private DateTime? _endDeadline;                       // merge window after a non-resumable dock
    private IReadOnlyList<Valetudo.Models.StatisticEntry>? _dockStats;   // robot's task statistics read at that dock
    private int _carriedTime;                             // statistics of earlier firmware tasks merged into this run
    private long _carriedArea;
    private int _taskFirstPhase;                          // index of the first phase of the current firmware task
    private (RecorderHint Hint, DateTime At)? _hint;
    private readonly HashSet<string> _activeSegments = [];
    private readonly Dictionary<string, string> _segmentNames = [];
    private string? _lastStatusKey;

    public CleaningRun? ActiveRun => Volatile.Read(ref _run);

    /// <summary>Raised when a run starts, ends or is recovered (thread-pool thread).</summary>
    public event Action? Changed;

    /// <summary>Raised after a run has been stored as Completed (not for discarded runs). Thread-pool thread.</summary>
    public event Action<CleaningRun>? RunCompleted;

    /// <summary>Called by the UI right before a cleaning command so the run gets the intended rooms/type.</summary>
    public void NoteCommand(RecorderHint hint)
    {
        lock (_events) _hint = (hint, DateTime.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        monitor.StateChanged += OnStateChanged;
        monitor.MapChanged += OnMapChanged;
        try
        {
            await RecoverAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _opts.CheckpointSeconds)));
            var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            var read = _events.Reader.ReadAsync(stoppingToken).AsTask();

            while (!stoppingToken.IsCancellationRequested)
            {
                var done = await Task.WhenAny(tick, read);
                try
                {
                    if (done == read)
                    {
                        var ev = await read;
                        read = _events.Reader.ReadAsync(stoppingToken).AsTask();
                        if (ev.State is { } s) await OnStateAsync(s, stoppingToken);
                        if (ev.Map is { } m) OnMap(m);
                    }
                    else
                    {
                        await tick;
                        tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                        await OnTickAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    log.LogError(e, "History recorder failed to process an event");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            monitor.StateChanged -= OnStateChanged;
            monitor.MapChanged -= OnMapChanged;
        }
    }

    private void OnStateChanged(RobotSnapshot s) => _events.Writer.TryWrite(new RobotEvent(s, null));
    private void OnMapChanged(MapSnapshot m) => _events.Writer.TryWrite(new RobotEvent(null, m));

    // ---- startup recovery -----------------------------------------------------

    private async Task RecoverAsync(CancellationToken ct)
    {
        while (monitor.State.IsEmpty) await Task.Delay(1000, ct);
        var state = monitor.State;
        var now = DateTime.UtcNow;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var open = await db.Runs.Include(r => r.Map).Where(r => r.State == RunState.InProgress).ToListAsync(ct);
            CleaningRun? adopt = null;
            if (open.Count == 1 && state.IsInCleaningTask && now - open[0].LastCheckpointUtc < AdoptWindow)
                adopt = open[0];

            foreach (var r in open)
            {
                if (r == adopt) continue;
                r.State = RunState.Aborted;
                r.EndedUtc = r.LastCheckpointUtc;
                r.DurationSeconds ??= (int)(r.LastCheckpointUtc - r.StartedUtc).TotalSeconds;
                log.LogInformation("Marked stale in-progress run {Id} as aborted", r.Id);
            }
            await db.SaveChangesAsync(ct);

            if (adopt is not null)
            {
                _run = adopt;
                _path = adopt.Map?.PathJsonGzip is { Length: > 0 } gz ? PathAccumulator.FromGzipJson(gz) : new PathAccumulator();
                foreach (var id in adopt.SegmentIds) _activeSegments.Add(id);
                _latestMapJson = monitor.Map?.Json;
                log.LogInformation("Adopted in-progress run {Id} started {Started:u}", adopt.Id, adopt.StartedUtc);
            }
        }

        if (_run is null && state.Status?.Value == "cleaning")
        {
            log.LogInformation("Robot is already cleaning at startup; recording a partial run");
            await StartRunAsync(state, startedBeforeAppStart: true, ct);
        }

        if (monitor.Map is { } map) OnMap(map);
        _lastStatusKey = StatusKey(state);
        Changed?.Invoke();
    }

    // ---- state machine ----------------------------------------------------------

    private async Task OnStateAsync(RobotSnapshot s, CancellationToken ct)
    {
        var status = s.Status;
        if (status is null) return;

        var key = StatusKey(s);
        var changed = key != _lastStatusKey;
        _lastStatusKey = key;
        if (changed) log.LogDebug("Robot status {Status}", key);

        var cleaning = status.Value == "cleaning";
        var terminal = status.Value is "docked" or "idle" or "error";
        var now = DateTime.UtcNow;

        if (_run is null)
        {
            if (cleaning) await StartRunAsync(s, startedBeforeAppStart: false, ct);
            return;
        }

        if (!cleaning && _endDeadline is { } end && now > end)
        {
            await EndRunAsync(s, ct);
            return;
        }

        if (cleaning)
        {
            if (_endDeadline is not null)
            {
                // The firmware finished a task and started the next one right away (vacuum-then-mop as two tasks,
                // a dust auto-empty stop): keep it as one run, carry the finished task's statistics.
                _endDeadline = null;
                _graceDeadline = null;
                CarryTaskStats();
                log.LogInformation("Run {Id}: cleaning resumed within the merge window, continuing the run", _run.Id);
                BeginPhase(s);
            }
            else if (_graceDeadline is not null)
            {
                // Back from a mid-task dock visit (mop wash, pad swap, charge): everything from here on is a new phase.
                _graceDeadline = null;
                BeginPhase(s);
            }
        }
        else if (terminal)
        {
            if (status.Flag == "resumable")
            {
                _endDeadline = null;
                if (_graceDeadline is null)
                {
                    _graceDeadline = now.AddMinutes(_opts.ResumableGraceMinutes);
                    ClosePhase(now, CumulativeArea(monitor.CurrentStats));
                    log.LogInformation("Run {Id}: robot {Status} but resumable, keeping run open until {Deadline:u}", _run.Id, status.Value, _graceDeadline);
                }
            }
            else if (status.Value == "error" || _opts.MergeWindowSeconds <= 0)
            {
                await EndRunAsync(s, ct);
                return;
            }
            else if (_endDeadline is null)
            {
                _graceDeadline = null;
                _endDeadline = now.AddSeconds(_opts.MergeWindowSeconds);
                _dockStats = await TryReadStatsAsync(ct);
                ClosePhase(now, CumulativeArea(_dockStats ?? monitor.CurrentStats));
                log.LogInformation("Run {Id}: robot {Status}; closing the run in {Seconds}s unless cleaning resumes", _run.Id, status.Value, _opts.MergeWindowSeconds);
            }
        }

        if (changed) await CheckpointAsync(ct);
    }

    private void OnMap(MapSnapshot m)
    {
        foreach (var seg in m.Summary.Segments)
            if (!string.IsNullOrWhiteSpace(seg.Name)) _segmentNames[seg.Id] = seg.Name!;

        if (_run is null) return;
        _latestMapJson = m.Json;
        _run.MapNonce = m.Nonce;
        _path.Observe(m.Summary.Paths);
        foreach (var seg in m.Summary.Segments)
            if (seg.Active) _activeSegments.Add(seg.Id);
    }

    private async Task OnTickAsync(CancellationToken ct)
    {
        if (_run is null) return;
        if (_graceDeadline is { } deadline && DateTime.UtcNow > deadline)
        {
            log.LogInformation("Run {Id}: resumable grace period expired, closing run", _run.Id);
            await EndRunAsync(monitor.State, ct);
            return;
        }
        if (_endDeadline is { } end && DateTime.UtcNow > end)
        {
            await EndRunAsync(monitor.State, ct);
            return;
        }
        await CheckpointAsync(ct);
    }

    private async Task StartRunAsync(RobotSnapshot s, bool startedBeforeAppStart, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var hint = TakeHint();

        var run = new CleaningRun
        {
            StartedUtc = now,
            LastCheckpointUtc = now,
            State = RunState.InProgress,
            RunType = RunTypeFrom(s.StatusFlag, hint),
            StartedBeforeAppStart = startedBeforeAppStart,
            FanPreset = s.Preset("fan_speed"),
            WaterPreset = s.Preset("water_grade"),
            OperationMode = s.Preset("operation_mode"),
            Map = new CleaningRunMap()
        };
        run.Phases.Add(NewPhase(0, now, s));

        _path = new PathAccumulator();
        _activeSegments.Clear();
        ResetTaskState();

        if (hint is SegmentCleanHint sc)
        {
            run.SegmentIds = sc.SegmentIds.ToList();
            run.SegmentsFromHint = true;
            foreach (var id in sc.SegmentIds) _activeSegments.Add(id);
        }

        var map = monitor.Map;
        _latestMapJson = map?.Json;
        if (map is not null)
        {
            run.MapNonce = map.Nonce;
            _path.Observe(map.Summary.Paths);
            foreach (var seg in map.Summary.Segments)
                if (seg.Active) _activeSegments.Add(seg.Id);
        }

        ApplySegments(run);
        run.Map.MapJsonGzip = GzipUtil.Compress(_latestMapJson);
        run.Map.PathJsonGzip = _path.ToGzipJson();

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
        }

        _run = run;
        log.LogInformation("Run {Id} started ({Type}, flag {Flag})", run.Id, run.RunType, s.StatusFlag);
        Changed?.Invoke();
    }

    private async Task CheckpointAsync(CancellationToken ct)
    {
        if (_run is null) return;
        var run = _run;
        run.LastCheckpointUtc = DateTime.UtcNow;
        ApplySegments(run);
        run.PathPointCount = _path.TotalPoints;
        run.PathSegmentCount = _path.Segments.Count;
        UpdatePhasePoints(run);
        run.Map ??= new CleaningRunMap();
        run.Map.MapJsonGzip = GzipUtil.Compress(_latestMapJson);
        run.Map.PathJsonGzip = _path.ToGzipJson();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Update(run);
        await db.SaveChangesAsync(ct);
        log.LogDebug("Run {Id} checkpoint: {Points} path points in {Segments} segments", run.Id, run.PathPointCount, run.PathSegmentCount);
    }

    private async Task EndRunAsync(RobotSnapshot s, CancellationToken ct)
    {
        if (_run is null) return;
        var run = _run;
        var now = DateTime.UtcNow;

        var stats = await TryReadStatsAsync(ct) ?? _dockStats;

        run.EndedUtc = now;
        ClosePhase(now, CumulativeArea(stats ?? monitor.CurrentStats));
        var reportedTime = stats?.FirstOrDefault(x => x.Type == "time")?.Value;
        var reportedArea = stats?.FirstOrDefault(x => x.Type == "area")?.Value;
        // The robot's counters only cover its current task; earlier tasks merged into this run were carried over.
        var lastTaskTime = reportedTime is > 0 ? (int)reportedTime.Value : run.Phases.Skip(_taskFirstPhase).Sum(p => p.DurationSeconds);
        run.DurationSeconds = _carriedTime + lastTaskTime;
        var area = _carriedArea + (reportedArea is > 0 ? (long)reportedArea.Value : 0);
        run.AreaCm2 = area > 0 ? area : null;
        run.EndStatus = s.Status?.Value;
        run.EndFlag = s.Status?.Flag;
        run.ErrorMessage = s.Status?.Error?.Message;
        ApplySegments(run);
        run.PathPointCount = _path.TotalPoints;
        run.PathSegmentCount = _path.Segments.Count;
        run.LastCheckpointUtc = now;
        UpdatePhasePoints(run);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (run.DurationSeconds < _opts.MinRunSeconds && run.PathPointCount < 10)
        {
            await db.RunMaps.Where(m => m.CleaningRunId == run.Id).ExecuteDeleteAsync(ct);
            await db.Runs.Where(r => r.Id == run.Id).ExecuteDeleteAsync(ct);
            log.LogInformation("Run {Id} discarded: only {Seconds}s and {Points} path points", run.Id, run.DurationSeconds, run.PathPointCount);
        }
        else
        {
            run.State = RunState.Completed;
            run.Map ??= new CleaningRunMap();
            run.Map.MapJsonGzip = GzipUtil.Compress(SubstitutePath(_latestMapJson, _path.Segments, run.Phases));
            run.Map.PathJsonGzip = _path.ToGzipJson();
            db.Update(run);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Run {Id} completed: {Seconds}s, {Area} m², {Points} path points, {Phases} phase(s) [{Kinds}], rooms [{Rooms}]",
                run.Id, run.DurationSeconds, run.AreaM2?.ToString("0.0") ?? "?", run.PathPointCount,
                run.Phases.Count, string.Join(", ", run.Phases.Select(p => p.Kind)), string.Join(", ", run.SegmentNames));
        }

        var completed = run.State == RunState.Completed ? run : null;
        _run = null;
        _path = new PathAccumulator();
        _activeSegments.Clear();
        ResetTaskState();
        Changed?.Invoke();
        if (completed is not null)
        {
            try { RunCompleted?.Invoke(completed); }
            catch (Exception e) { log.LogWarning(e, "RunCompleted subscriber failed"); }
        }
    }

    // ---- phases -------------------------------------------------------------------

    private static RunPhase NewPhase(int index, DateTime startedUtc, RobotSnapshot s)
    {
        var mode = s.Preset("operation_mode");
        var mop = s.Attachments.FirstOrDefault(a => string.Equals(a.Type, "mop", StringComparison.OrdinalIgnoreCase))?.Attached;
        return new RunPhase
        {
            Index = index,
            Kind = PhaseKinds.Classify(mode, mop, index),
            StartedUtc = startedUtc,
            OperationMode = mode,
            MopAttached = mop
        };
    }

    private void BeginPhase(RobotSnapshot s)
    {
        if (_run is null) return;
        var now = DateTime.UtcNow;
        ClosePhase(now, CumulativeArea(monitor.CurrentStats));
        var phase = NewPhase(_run.Phases.Count, now, s);
        _run.Phases.Add(phase);
        _path.BeginPhase(phase.Index);
        log.LogInformation("Run {Id}: phase {Index} started ({Kind}; mode {Mode}, mop attached: {Mop})",
            _run.Id, phase.Index + 1, phase.Kind, phase.OperationMode ?? "?", phase.MopAttached?.ToString() ?? "?");
    }

    /// <summary>Marks the current phase as ended (idempotent) and attributes the area gained since the previous phase to it.</summary>
    private void ClosePhase(DateTime now, long? cumulativeAreaCm2)
    {
        if (_run is null || _run.Phases.Count == 0) return;
        var phase = _run.Phases[^1];
        if (phase.EndedUtc is not null) return;
        phase.EndedUtc = now;
        phase.DurationSeconds = (int)(now - phase.StartedUtc).TotalSeconds;
        if (cumulativeAreaCm2 is { } cum)
        {
            // The robot's area counter is cumulative within one firmware task; earlier phases of that task were already credited.
            var before = _run.Phases.Skip(_taskFirstPhase).Take(_run.Phases.Count - 1 - _taskFirstPhase).Sum(p => p.AreaCm2 ?? 0);
            // A negative delta means the robot restarted its counters at the dock visit; take the raw value then.
            phase.AreaCm2 = cum >= before ? cum - before : cum;
        }
    }

    /// <summary>The firmware closed a task and starts another inside the same run: bank that task's statistics.</summary>
    private void CarryTaskStats()
    {
        if (_run is null) return;
        var time = _dockStats?.FirstOrDefault(x => x.Type == "time")?.Value;
        var area = _dockStats?.FirstOrDefault(x => x.Type == "area")?.Value;
        _carriedTime += time is > 0 ? (int)time.Value : _run.Phases.Skip(_taskFirstPhase).Sum(p => p.DurationSeconds);
        _carriedArea += area is > 0 ? (long)area.Value : 0;
        _taskFirstPhase = _run.Phases.Count;
        _dockStats = null;
    }

    private void ResetTaskState()
    {
        _graceDeadline = null;
        _endDeadline = null;
        _dockStats = null;
        _carriedTime = 0;
        _carriedArea = 0;
        _taskFirstPhase = 0;
    }

    private async Task<IReadOnlyList<Valetudo.Models.StatisticEntry>?> TryReadStatsAsync(CancellationToken ct)
    {
        if (!monitor.HasCapability(Valetudo.Models.Capabilities.CurrentStatistics)) return null;
        try { return await client.GetCurrentStatisticsAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Could not read current statistics: {Message}", e.Message);
            return null;
        }
    }

    private void UpdatePhasePoints(CleaningRun run)
    {
        foreach (var phase in run.Phases) phase.PathPointCount = _path.PointsInPhase(phase.Index);
    }

    private static long? CumulativeArea(IReadOnlyList<Valetudo.Models.StatisticEntry>? stats)
    {
        var area = stats?.FirstOrDefault(x => x.Type == "area")?.Value;
        return area is > 0 ? (long)area.Value : null;
    }

    // ---- helpers ------------------------------------------------------------------

    private void ApplySegments(CleaningRun run)
    {
        if (!run.SegmentsFromHint)
            run.SegmentIds = _activeSegments.OrderBy(id => int.TryParse(id, out var n) ? n : int.MaxValue).ThenBy(id => id).ToList();
        run.SegmentNames = run.SegmentIds.Select(id => _segmentNames.TryGetValue(id, out var n) ? n : $"Room {id}").ToList();
    }

    private RecorderHint? TakeHint()
    {
        lock (_events)
        {
            var h = _hint;
            _hint = null;
            return h is { } v && DateTime.UtcNow - v.At < HintWindow ? v.Hint : null;
        }
    }

    private static string RunTypeFrom(string flag, RecorderHint? hint) => hint switch
    {
        SegmentCleanHint => "segment",
        ZoneCleanHint => "zone",
        _ => flag switch
        {
            "segment" => "segment",
            "zone" => "zone",
            "spot" => "spot",
            "mapping" => "mapping",
            _ => "full"
        }
    };

    private static string StatusKey(RobotSnapshot s) => $"{s.StatusValue}/{s.StatusFlag}";

    /// <summary>
    /// Replaces the map's path entities with the accumulated path: one PathMapEntity per segment, tagged with
    /// <c>metaData.phase</c> (index) and <c>metaData.phaseKind</c> so the renderer can colour and filter them.
    /// </summary>
    internal static string? SubstitutePath(string? mapJson, IReadOnlyList<PathSegment> segments, IReadOnlyList<RunPhase> phases)
    {
        if (string.IsNullOrEmpty(mapJson)) return mapJson;
        if (JsonNode.Parse(mapJson) is not JsonObject root) return mapJson;

        var entities = root["entities"] as JsonArray;
        if (entities is null)
        {
            entities = [];
            root["entities"] = entities;
        }

        for (var i = entities.Count - 1; i >= 0; i--)
        {
            var type = entities[i]?["type"]?.GetValue<string>();
            if (type is "path" or "predicted_path") entities.RemoveAt(i);
        }

        foreach (var seg in segments)
        {
            var kind = phases.FirstOrDefault(p => p.Index == seg.Phase)?.Kind ?? PhaseKinds.Unknown;
            entities.Add(new JsonObject
            {
                ["__class"] = "PathMapEntity",
                ["metaData"] = new JsonObject { ["phase"] = seg.Phase, ["phaseKind"] = kind },
                ["points"] = new JsonArray(seg.Points.Select(v => (JsonNode)v).ToArray()),
                ["type"] = "path"
            });
        }

        return root.ToJsonString();
    }
}
