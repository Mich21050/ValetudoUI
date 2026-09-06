using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValetudoUI.Valetudo.Models;

/// <summary>Base type for Valetudo state attributes. Discriminated by the "__class" JSON property.</summary>
public abstract record StateAttribute;

public sealed record StatusError(string? Message, string? Subsystem, JsonElement? Severity, JsonElement? VendorErrorCode);

/// <summary>value: error|docked|idle|returning|cleaning|paused|manual_control|moving; flag: none|zone|segment|spot|target|resumable|mapping</summary>
public sealed record StatusStateAttribute(string Value, string Flag, StatusError? Error) : StateAttribute;

/// <summary>flag: none|charging|discharging|charged</summary>
public sealed record BatteryStateAttribute(int Level, string Flag) : StateAttribute;

/// <summary>type: dustbin|watertank|mop</summary>
public sealed record AttachmentStateAttribute(string Type, bool Attached) : StateAttribute;

/// <summary>type: fan_speed|water_grade|operation_mode</summary>
public sealed record PresetSelectionStateAttribute(string Type, string Value) : StateAttribute;

/// <summary>value: error|idle|pause|emptying|cleaning|drying</summary>
public sealed record DockStatusStateAttribute(string Value) : StateAttribute;

/// <summary>type: water_tank_clean|water_tank_dirty|dustbag|detergent; value: ok|missing|empty|full|unknown</summary>
public sealed record DockComponentStateAttribute(string Type, string Value) : StateAttribute;

public sealed record UnknownStateAttribute(string Class, JsonElement Raw) : StateAttribute;

public static class ValetudoJson
{
    /// <summary>Options for everything coming from / going to Valetudo.</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Same as <see cref="Options"/> but without the attribute converter (used inside the converter).</summary>
    internal static readonly JsonSerializerOptions Inner = new() { PropertyNameCaseInsensitive = true };

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        o.Converters.Add(new StateAttributeConverter());
        return o;
    }
}

public sealed class StateAttributeConverter : JsonConverter<StateAttribute>
{
    public override StateAttribute Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // ParseValue returns an element backed by its own document, safe to keep around.
        var el = JsonElement.ParseValue(ref reader);
        var cls = el.TryGetProperty("__class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var inner = ValetudoJson.Inner;
        return cls switch
        {
            "StatusStateAttribute" => el.Deserialize<StatusStateAttribute>(inner)!,
            "BatteryStateAttribute" => el.Deserialize<BatteryStateAttribute>(inner)!,
            "AttachmentStateAttribute" => el.Deserialize<AttachmentStateAttribute>(inner)!,
            "PresetSelectionStateAttribute" => el.Deserialize<PresetSelectionStateAttribute>(inner)!,
            "DockStatusStateAttribute" => el.Deserialize<DockStatusStateAttribute>(inner)!,
            "DockComponentStateAttribute" => el.Deserialize<DockComponentStateAttribute>(inner)!,
            _ => new UnknownStateAttribute(cls ?? "?", el)
        };
    }

    public override void Write(Utf8JsonWriter writer, StateAttribute value, JsonSerializerOptions options)
        => throw new NotSupportedException("State attributes are never serialised.");
}
