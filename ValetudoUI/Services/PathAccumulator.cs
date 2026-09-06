using System.Text.Json;
using ValetudoUI.Data;

namespace ValetudoUI.Services;

/// <summary>
/// Collects the robot's path across map updates. Valetudo's "path" entity normally only grows during a run,
/// but the firmware may reset it; each reset starts a new segment so nothing is lost and no bogus
/// straight line is drawn between unrelated points.
/// </summary>
public sealed class PathAccumulator
{
    /// <summary>Path segments as flat [x0,y0,x1,y1,...] arrays in cm.</summary>
    public List<int[]> Segments { get; } = [];

    private int[] _lastSeen = [];
    private bool _grew;

    public int TotalPoints => Segments.Sum(s => s.Length) / 2;

    public void Observe(int[] points)
    {
        if (points.Length < 2) return;

        if (Segments.Count == 0)
        {
            Segments.Add(points);
            _lastSeen = points;
            return;
        }

        if (points.Length >= _lastSeen.Length && IsPrefix(_lastSeen, points))
        {
            // Normal growth: replace the current segment with the longer version.
            if (points.Length > _lastSeen.Length) _grew = true;
            Segments[^1] = points;
            _lastSeen = points;
            return;
        }

        if (IsPrefix(points, _lastSeen))
        {
            // Shorter but same start: transient glitch, keep what we have.
            return;
        }

        // A different path: the firmware reset it. If nothing we collected so far ever grew during this run,
        // it was the previous run's leftover path being replaced, so drop it; otherwise keep it as a segment.
        if (!_grew) Segments.Clear();
        Segments.Add(points);
        _lastSeen = points;
    }

    private static bool IsPrefix(int[] prefix, int[] full)
    {
        if (prefix.Length > full.Length) return false;
        return prefix.AsSpan().SequenceEqual(full.AsSpan(0, prefix.Length));
    }

    public byte[] ToGzipJson() => GzipUtil.Compress(JsonSerializer.SerializeToUtf8Bytes(Segments));

    public static PathAccumulator FromGzipJson(byte[] gzip)
    {
        var acc = new PathAccumulator();
        if (gzip.Length == 0) return acc;
        var segments = JsonSerializer.Deserialize<List<int[]>>(GzipUtil.Decompress(gzip)) ?? [];
        acc.Segments.AddRange(segments.Where(s => s.Length >= 2));
        if (acc.Segments.Count > 0)
        {
            acc._lastSeen = acc.Segments[^1];
            acc._grew = true;
        }
        return acc;
    }
}
