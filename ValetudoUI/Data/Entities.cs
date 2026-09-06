namespace ValetudoUI.Data;

public enum RunState { InProgress, Completed, Aborted }

public sealed class CleaningRun
{
    public int Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public DateTime LastCheckpointUtc { get; set; }
    public RunState State { get; set; }

    /// <summary>full | segment | zone | spot | mapping</summary>
    public string RunType { get; set; } = "full";

    /// <summary>The app was started while this run was already going; start time and path are partial.</summary>
    public bool StartedBeforeAppStart { get; set; }

    public int? DurationSeconds { get; set; }
    public long? AreaCm2 { get; set; }

    public List<string> SegmentIds { get; set; } = [];
    public List<string> SegmentNames { get; set; } = [];
    public bool SegmentsFromHint { get; set; }

    public string? FanPreset { get; set; }
    public string? WaterPreset { get; set; }
    public string? OperationMode { get; set; }

    public string? EndStatus { get; set; }
    public string? EndFlag { get; set; }
    public string? ErrorMessage { get; set; }

    public string? MapNonce { get; set; }
    public int PathPointCount { get; set; }
    public int PathSegmentCount { get; set; }

    public CleaningRunMap? Map { get; set; }

    public double? AreaM2 => AreaCm2 is { } a ? a / 10000.0 : null;
}

/// <summary>Blob storage for a run, in its own table so list queries stay light.</summary>
public sealed class CleaningRunMap
{
    public int CleaningRunId { get; set; }

    /// <summary>Gzip of the ValetudoMap JSON with the path entities replaced by the accumulated path (empty when unknown).</summary>
    public byte[] MapJsonGzip { get; set; } = [];

    /// <summary>Gzip of the accumulated path segments as JSON int[][] (cm coordinates).</summary>
    public byte[]? PathJsonGzip { get; set; }

    public CleaningRun Run { get; set; } = null!;
}
