using ValetudoUI.Valetudo.Models;

namespace ValetudoUI.Services;

/// <summary>Immutable view of the robot's state attributes at a point in time.</summary>
public sealed record RobotSnapshot(IReadOnlyList<StateAttribute> Attributes, DateTime UpdatedUtc)
{
    public static readonly RobotSnapshot Empty = new([], DateTime.MinValue);

    public bool IsEmpty => Attributes.Count == 0;
    public StatusStateAttribute? Status => Attributes.OfType<StatusStateAttribute>().FirstOrDefault();
    public BatteryStateAttribute? Battery => Attributes.OfType<BatteryStateAttribute>().FirstOrDefault();
    public DockStatusStateAttribute? Dock => Attributes.OfType<DockStatusStateAttribute>().FirstOrDefault();
    public IEnumerable<AttachmentStateAttribute> Attachments => Attributes.OfType<AttachmentStateAttribute>();
    public IEnumerable<DockComponentStateAttribute> DockComponents => Attributes.OfType<DockComponentStateAttribute>();

    public string? Preset(string type)
        => Attributes.OfType<PresetSelectionStateAttribute>().FirstOrDefault(p => p.Type == type)?.Value;

    public string StatusValue => Status?.Value ?? "unknown";
    public string StatusFlag => Status?.Flag ?? "none";

    /// <summary>Robot is doing something (cleaning, returning, moving, manual control).</summary>
    public bool IsActive => Status?.Value is "cleaning" or "returning" or "manual_control" or "moving";

    /// <summary>A cleaning task is open: cleaning, paused or on its way home.</summary>
    public bool IsInCleaningTask => Status?.Value is "cleaning" or "paused" or "returning";
}

public sealed record MapSegment(string Id, string? Name, bool Active, long AreaCm2)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Room {Id}" : Name;
}

/// <summary>The few parts of the map JSON the server needs (the browser decodes the rest).</summary>
public sealed record MapSummary(
    string Nonce,
    int PixelSize,
    IReadOnlyList<MapSegment> Segments,
    IReadOnlyList<int[]> Paths,
    MapPoint? RobotPosition,
    double? RobotAngle,
    MapPoint? ChargerPosition);

public sealed record MapSnapshot(string Json, string Nonce, long Version, MapSummary Summary, DateTime UpdatedUtc);

/// <summary>One of State or Map is set.</summary>
public sealed record RobotEvent(RobotSnapshot? State, MapSnapshot? Map);
