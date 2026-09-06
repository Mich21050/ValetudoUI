namespace ValetudoUI.Valetudo.Models;

public sealed record RobotInfo(string? Manufacturer, string? ModelName, string? Implementation);

/// <summary>Segment (room) as returned by MapSegmentationCapability. Ids are strings in Valetudo.</summary>
public sealed record SegmentInfo(string Id, string? Name);

public sealed record IterationCountRange(int Min, int Max);

public sealed record SegmentationProperties(IterationCountRange? IterationCount, bool CustomOrderSupport);

public sealed record ZoneProperties(IterationCountRange? ZoneCount, IterationCountRange? IterationCount);

public sealed record ConsumableRemaining(double Value, string Unit);

/// <summary>type: filter|brush|mop|detergent|bin|cleaning; subType: none|all|main|secondary|side_left|side_right|dock|sensor|wheel; unit: minutes|percent</summary>
public sealed record Consumable(string Type, string SubType, ConsumableRemaining Remaining);

/// <summary>type: time (seconds) | area (cm²) | count</summary>
public sealed record StatisticEntry(string Type, double Value);

/// <summary>Axis-aligned rectangle in map coordinates (integer cm).</summary>
public sealed record ZoneRect(int X1, int Y1, int X2, int Y2);

public sealed record MapPoint(int X, int Y);

public enum BasicControlAction { Start, Stop, Pause, Home }

public enum PresetCapability { FanSpeed, WaterUsage, OperationMode }

public static class Capabilities
{
    public const string BasicControl = "BasicControlCapability";
    public const string MapSegmentation = "MapSegmentationCapability";
    public const string FanSpeedControl = "FanSpeedControlCapability";
    public const string WaterUsageControl = "WaterUsageControlCapability";
    public const string OperationModeControl = "OperationModeControlCapability";
    public const string ZoneCleaning = "ZoneCleaningCapability";
    public const string GoToLocation = "GoToLocationCapability";
    public const string Locate = "LocateCapability";
    public const string ConsumableMonitoring = "ConsumableMonitoringCapability";
    public const string CurrentStatistics = "CurrentStatisticsCapability";
    public const string TotalStatistics = "TotalStatisticsCapability";
    public const string AutoEmptyDockManualTrigger = "AutoEmptyDockManualTriggerCapability";
    public const string MopDockCleanManualTrigger = "MopDockCleanManualTriggerCapability";
    public const string MopDockDryManualTrigger = "MopDockDryManualTriggerCapability";
}

public static class PresetCapabilityExtensions
{
    public static string CapabilityName(this PresetCapability p) => p switch
    {
        PresetCapability.FanSpeed => Capabilities.FanSpeedControl,
        PresetCapability.WaterUsage => Capabilities.WaterUsageControl,
        PresetCapability.OperationMode => Capabilities.OperationModeControl,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    /// <summary>The "type" of the matching PresetSelectionStateAttribute.</summary>
    public static string AttributeType(this PresetCapability p) => p switch
    {
        PresetCapability.FanSpeed => "fan_speed",
        PresetCapability.WaterUsage => "water_grade",
        PresetCapability.OperationMode => "operation_mode",
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    public static string Label(this PresetCapability p) => p switch
    {
        PresetCapability.FanSpeed => "Suction",
        PresetCapability.WaterUsage => "Water",
        PresetCapability.OperationMode => "Mode",
        _ => p.ToString()
    };
}
