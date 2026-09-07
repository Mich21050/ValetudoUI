using System.Text.Json;
using System.Text.Json.Serialization;
using ValetudoUI.Data;

namespace ValetudoUI.Services;

/// <summary>A stretch of the robot's path: flat [x0,y0,x1,y1,...] in cm, tagged with the run phase it belongs to.</summary>
public sealed record PathSegment(int Phase, int[] Points);

/// <summary>
/// Collects the robot's path across map updates. Valetudo reports the path as a list of polylines ("path" entities);
/// a new one starts wherever the robot's track has a break (dock trips, relocations). Between updates the last
/// polyline grows and its final point (the live robot position) is usually rewritten by a few cm, new polylines get
/// appended, and the whole list is reset when a new task starts. Every polyline is kept as its own segment so no
/// straight line is ever drawn across a break, and <see cref="BeginPhase"/> splits the track at a dock visit so the
/// vacuum and mop passes of one run can be told apart.
/// </summary>
public sealed class PathAccumulator
{
    public List<PathSegment> Segments { get; } = [];

    public int CurrentPhase { get; private set; }

    private List<int[]> _lastSeen = [];   // the polylines as last reported by the robot
    private List<int> _segOf = [];        // polyline index -> index in Segments
    private List<int> _offsets = [];      // polyline index -> first coordinate index that belongs to its current segment
    private bool _grew;
    private bool _breakPending;

    public int TotalPoints => Segments.Sum(s => s.Points.Length) / 2;

    public int PointsInPhase(int phase) => Segments.Where(s => s.Phase == phase).Sum(s => s.Points.Length) / 2;

    /// <summary>From now on new path points belong to <paramref name="phase"/>; the next growth starts a new segment.</summary>
    public void BeginPhase(int phase)
    {
        CurrentPhase = phase;
        _breakPending = true;
    }

    public void Observe(IReadOnlyList<int[]> polylines)
    {
        var incoming = polylines.Where(p => p.Length >= 2).ToList();
        if (incoming.Count == 0) return;

        if (_lastSeen.Count == 0)
        {
            Reset(incoming);
            return;
        }

        var k = _lastSeen.Count - 1;
        var compatible = incoming.Count >= _lastSeen.Count;
        for (var i = 0; compatible && i < k; i++)
            compatible = _lastSeen[i].AsSpan().SequenceEqual(incoming[i]);
        if (compatible) compatible = IsPrefixIgnoringTail(_lastSeen[k], incoming[k]);

        if (compatible)
        {
            if (incoming[k].Length > _lastSeen[k].Length || incoming.Count > _lastSeen.Count) _grew = true;

            if (_breakPending && incoming[k].Length > _lastSeen[k].Length)
            {
                // The last polyline kept growing after the phase break: split it at the last point of the old phase
                // so the line stays connected.
                var start = Math.Max(0, _lastSeen[k].Length - 2);
                Segments.Add(new PathSegment(CurrentPhase, incoming[k][start..]));
                _segOf[k] = Segments.Count - 1;
                _offsets[k] = start;
                _breakPending = false;
            }
            else
            {
                Segments[_segOf[k]] = Segments[_segOf[k]] with { Points = incoming[k][_offsets[k]..] };
            }

            for (var j = k + 1; j < incoming.Count; j++)
            {
                Segments.Add(new PathSegment(CurrentPhase, incoming[j]));
                _segOf.Add(Segments.Count - 1);
                _offsets.Add(0);
                _breakPending = false;
            }
            _lastSeen = incoming;
            return;
        }

        if (IsShrunkVersion(incoming, _lastSeen))
        {
            // Fewer/shorter polylines with the same start: transient glitch, keep what we have.
            return;
        }

        // A different track: the firmware reset it. If nothing we collected so far ever grew during this run,
        // it was the previous run's leftover path being replaced, so drop it; otherwise keep it.
        if (!_grew) Segments.Clear();
        Reset(incoming);
    }

    private void Reset(List<int[]> incoming)
    {
        _segOf = [];
        _offsets = [];
        foreach (var p in incoming)
        {
            Segments.Add(new PathSegment(CurrentPhase, p));
            _segOf.Add(Segments.Count - 1);
            _offsets.Add(0);
        }
        _lastSeen = incoming;
        _breakPending = false;
    }

    /// <summary>True when <paramref name="next"/> equals <paramref name="prev"/> except for prev's last point and appended points.</summary>
    private static bool IsPrefixIgnoringTail(int[] prev, int[] next)
    {
        if (next.Length < prev.Length) return false;
        var n = Math.Max(0, prev.Length - 2);
        return prev.AsSpan(0, n).SequenceEqual(next.AsSpan(0, n));
    }

    private static bool IsShrunkVersion(List<int[]> incoming, List<int[]> lastSeen)
    {
        if (incoming.Count > lastSeen.Count) return false;
        for (var i = 0; i < incoming.Count; i++)
            if (!IsPrefixIgnoringTail(incoming[i], lastSeen[i])) return false;
        return true;
    }

    // ---- persistence --------------------------------------------------------------

    private sealed record Stored(int V, List<StoredSegment> Segments, List<int[]>? LastSeen, List<int>? SegOf, List<int>? Offsets, int Phase);
    private sealed record StoredSegment(int Phase, int[] Points);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public byte[] ToGzipJson() => GzipUtil.Compress(JsonSerializer.SerializeToUtf8Bytes(
        new Stored(3, Segments.Select(s => new StoredSegment(s.Phase, s.Points)).ToList(), _lastSeen, _segOf, _offsets, CurrentPhase), Json));

    public static PathAccumulator FromGzipJson(byte[] gzip)
    {
        var acc = new PathAccumulator();
        if (gzip.Length == 0) return acc;
        var stored = JsonSerializer.Deserialize<Stored>(GzipUtil.Decompress(gzip), Json);
        if (stored is null) return acc;

        acc.Segments.AddRange(stored.Segments.Where(s => s.Points.Length >= 2).Select(s => new PathSegment(s.Phase, s.Points)));
        acc.CurrentPhase = stored.Phase;
        acc._grew = acc.Segments.Count > 0;

        // Restore the matching state so the next update continues the same segments instead of duplicating them.
        if (stored.LastSeen is { } seen && stored.SegOf is { } segOf && stored.Offsets is { } offsets
            && seen.Count == segOf.Count && seen.Count == offsets.Count && segOf.All(i => i >= 0 && i < acc.Segments.Count))
        {
            acc._lastSeen = seen;
            acc._segOf = segOf;
            acc._offsets = offsets;
        }
        return acc;
    }
}
