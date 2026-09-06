using System.Text.Json.Serialization;

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

    /// <summary>
    /// Phases of the run, split at every dock visit the robot made while its task was still resumable
    /// (mop wash, pad swap, mid-task charge). Stored as JSON.
    /// </summary>
    public List<RunPhase> Phases { get; set; } = [];

    public CleaningRunMap? Map { get; set; }

    public double? AreaM2 => AreaCm2 is { } a ? a / 10000.0 : null;
}

/// <summary>One stretch of cleaning between two dock visits.</summary>
public sealed class RunPhase
{
    public int Index { get; set; }

    /// <summary>vacuum | mop | vacuum_and_mop | unknown (see <see cref="PhaseKinds"/>).</summary>
    public string Kind { get; set; } = PhaseKinds.Unknown;

    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }

    /// <summary>Operation mode preset the robot reported when the phase began.</summary>
    public string? OperationMode { get; set; }

    /// <summary>Whether the mop attachment was reported as attached when the phase began.</summary>
    public bool? MopAttached { get; set; }

    public int DurationSeconds { get; set; }

    /// <summary>Area the robot's current-run statistics grew by during this phase, when known.</summary>
    public long? AreaCm2 { get; set; }

    public int PathPointCount { get; set; }

    [JsonIgnore]
    public double? AreaM2 => AreaCm2 is { } a ? a / 10000.0 : null;
}

public static class PhaseKinds
{
    public const string Vacuum = "vacuum";
    public const string Mop = "mop";
    public const string VacuumAndMop = "vacuum_and_mop";
    public const string Unknown = "unknown";

    /// <summary>
    /// Guesses what a phase does. Explicit single modes win; for sequential modes (vacuum then mop) or when the
    /// mode is unknown, a detached mop means vacuuming, otherwise the first phase vacuums and later ones mop.
    /// </summary>
    public static string Classify(string? operationMode, bool? mopAttached, int index)
    {
        switch (operationMode?.Trim().ToLowerInvariant())
        {
            case "vacuum": return Vacuum;
            case "mop": return Mop;
            case "vacuum_and_mop": return VacuumAndMop;
        }
        if (mopAttached == false) return Vacuum;
        return index == 0 ? Vacuum : Mop;
    }
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
