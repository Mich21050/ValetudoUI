using System.Globalization;
using ValetudoUI.Data;
using ValetudoUI.Valetudo.Models;

namespace ValetudoUI.Components.Shared;

/// <summary>Display helpers shared by the components.</summary>
public static class Format
{
    public static string Status(string value) => value switch
    {
        "manual_control" => "Manual control",
        "unknown" => "Unknown",
        _ => Capitalise(value.Replace('_', ' '))
    };

    public static string Flag(string flag) => flag switch
    {
        "segment" => "Rooms",
        "zone" => "Zone",
        "spot" => "Spot",
        "target" => "Go to",
        "resumable" => "Resumable",
        "mapping" => "Mapping",
        _ => Capitalise(flag)
    };

    public static string Preset(string p) => p switch
    {
        "vacuum_and_mop" => "Vacuum & mop",
        "vacuum_then_mop" => "Vacuum then mop",
        _ => Capitalise(p.Replace('_', ' '))
    };

    public static string Attachment(string type) => type switch
    {
        "dustbin" => "Dust bin",
        "watertank" => "Water tank",
        _ => Capitalise(type)
    };

    public static string DockComponent(string type) => Capitalise(type.Replace('_', ' '));

    /// <summary>Bootstrap Icons class for the battery state.</summary>
    public static string BatteryIcon(BatteryStateAttribute b) => b.Flag switch
    {
        "charging" => "bi-battery-charging",
        _ => b.Level >= 80 ? "bi-battery-full" : b.Level > 20 ? "bi-battery-half" : "bi-battery"
    };

    public static string StatLabel(string type) => type switch
    {
        "time" => "Time",
        "area" => "Area",
        "count" => "Runs",
        _ => Capitalise(type)
    };

    public static string Stat(string type, double value) => type switch
    {
        "time" => Duration((int)value),
        "area" => Area((long)value),
        _ => value.ToString("0", CultureInfo.InvariantCulture)
    };

    public static string Duration(int? seconds)
    {
        if (seconds is null) return "–";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    public static string Area(long? cm2) => cm2 is null ? "–" : (cm2.Value / 10000.0).ToString("0.0", CultureInfo.InvariantCulture) + " m²";

    public static string Consumable(Valetudo.Models.Consumable c)
    {
        var type = c.Type switch
        {
            "brush" => "brush",
            "filter" => "filter",
            "mop" => "mop",
            "detergent" => "detergent",
            "bin" => "bin",
            "cleaning" => "cleaning",
            _ => c.Type
        };
        var sub = c.SubType switch
        {
            "none" or "all" => "",
            "main" => "Main ",
            "secondary" => "Secondary ",
            "side_left" => "Left side ",
            "side_right" => "Right side ",
            "dock" => "Dock ",
            "sensor" => "Sensor ",
            "wheel" => "Wheel ",
            _ => Capitalise(c.SubType) + " "
        };
        return Capitalise(sub + type);
    }

    public static string Remaining(ConsumableRemaining r) => r.Unit switch
    {
        "minutes" => r.Value >= 60 ? $"{r.Value / 60:0} h" : $"{r.Value:0} min",
        "percent" => $"{r.Value:0}%",
        _ => $"{r.Value:0} {r.Unit}"
    };

    public static string RunType(string type) => type switch
    {
        "segment" => "Rooms",
        "zone" => "Zone",
        "spot" => "Spot",
        "mapping" => "Mapping",
        _ => "Full clean"
    };

    public static string RunState(RunState state) => state switch
    {
        Data.RunState.InProgress => "In progress",
        Data.RunState.Aborted => "Aborted",
        _ => "Completed"
    };

    public static string RunStateClass(RunState state) => state switch
    {
        Data.RunState.InProgress => "info",
        Data.RunState.Aborted => "warn",
        _ => "ok"
    };

    public static string LocalDate(DateTime utc) => utc.ToLocalTime().ToString("ddd, d MMM yyyy", CultureInfo.InvariantCulture);
    public static string LocalTime(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    public static string LocalDateTime(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
