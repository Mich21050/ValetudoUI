using System.Text.Json;
using ValetudoUI.Valetudo.Models;

namespace ValetudoUI.Services;

public static class MapSummaryParser
{
    public static MapSummary Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var nonce = "";
        if (root.TryGetProperty("metaData", out var md) && md.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String)
            nonce = n.GetString() ?? "";

        var pixelSize = root.TryGetProperty("pixelSize", out var ps) && ps.TryGetInt32(out var psv) ? psv : 5;

        var segments = new List<MapSegment>();
        if (root.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
        {
            foreach (var layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("type", out var t) || t.GetString() != "segment") continue;
                if (!layer.TryGetProperty("metaData", out var m)) continue;
                if (!m.TryGetProperty("segmentId", out var sid)) continue;
                var id = sid.ValueKind == JsonValueKind.String ? sid.GetString() : sid.GetRawText();
                if (string.IsNullOrEmpty(id)) continue;
                var name = m.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
                var active = m.TryGetProperty("active", out var ac) && ac.ValueKind == JsonValueKind.True;
                long area = m.TryGetProperty("area", out var ar) && ar.TryGetInt64(out var av) ? av : 0;
                segments.Add(new MapSegment(id, name, active, area));
            }
        }

        var paths = new List<int[]>();
        MapPoint? robot = null, charger = null;
        double? angle = null;
        if (root.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entities.EnumerateArray())
            {
                if (!e.TryGetProperty("type", out var t)) continue;
                var type = t.GetString();
                if (!e.TryGetProperty("points", out var pts) || pts.ValueKind != JsonValueKind.Array) continue;
                switch (type)
                {
                    case "path":
                    {
                        // One polyline per entity: Valetudo starts a new one at every break in the robot's track.
                        var points = new List<int>(pts.GetArrayLength());
                        foreach (var p in pts.EnumerateArray()) points.Add(p.TryGetInt32(out var v) ? v : (int)p.GetDouble());
                        if (points.Count >= 2) paths.Add(points.ToArray());
                        break;
                    }
                    case "robot_position":
                        robot = ReadPoint(pts);
                        if (e.TryGetProperty("metaData", out var rm) && rm.TryGetProperty("angle", out var a) && a.TryGetDouble(out var ad))
                            angle = ad;
                        break;
                    case "charger_location":
                        charger = ReadPoint(pts);
                        break;
                }
            }
        }

        return new MapSummary(nonce, pixelSize, segments, paths, robot, angle, charger);
    }

    private static MapPoint? ReadPoint(JsonElement pts)
    {
        if (pts.GetArrayLength() < 2) return null;
        var x = pts[0]; var y = pts[1];
        return new MapPoint((int)x.GetDouble(), (int)y.GetDouble());
    }
}
