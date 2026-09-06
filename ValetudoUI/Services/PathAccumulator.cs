using System.Text.Json;
using System.Text.Json.Serialization;
using ValetudoUI.Data;

namespace ValetudoUI.Services;

/// <summary>A stretch of the robot's path: flat [x0,y0,x1,y1,...] in cm, tagged with the run phase it belongs to.</summary>
public sealed record PathSegment(int Phase, int[] Points);

/// <summary>
/// Collects the robot's path across map updates. Valetudo's "path" entity normally only grows during a run,
/// but the firmware may reset it; each reset starts a new segment so nothing is lost and no bogus
/// straight line is drawn between unrelated points. <see cref="BeginPhase"/> splits the path at a dock visit
/// so the vacuum and mop passes of one run can be told apart.
/// </summary>
public sealed class PathAccumulator
{
    public List<PathSegment> Segments { get; } = [];

    public int CurrentPhase { get; private set; }

    private int[] _lastSeen = [];   // the raw path as last reported by the robot
    private int _offset;            // index into the raw path where the current segment starts
    private bool _grew;
    private bool _breakPending;

    public int TotalPoints => Segments.Sum(s => s.Points.Length) / 2;

    public int PointsInPhase(int phase) => Segments.Where(s => s.Phase == phase).Sum(s => s.Points.Length) / 2;

    /// <summary>From now on new path points belong to <paramref name="phase"/>; the next observation starts a new segment.</summary>
    public void BeginPhase(int phase)
    {
        CurrentPhase = phase;
        _breakPending = true;
    }

    public void Observe(int[] points)
    {
        if (points.Length < 2) return;

        if (Segments.Count == 0)
        {
            Segments.Add(new PathSegment(CurrentPhase, points));
            _lastSeen = points;
            _offset = 0;
            _breakPending = false;
            return;
        }

        if (points.Length >= _lastSeen.Length && IsPrefix(_lastSeen, points))
        {
            if (points.Length == _lastSeen.Length) return;
            // Normal growth: extend the current segment, or start the next phase's segment at the last known point
            // so that the line stays connected.
            _grew = true;
            if (_breakPending)
            {
                _offset = Math.Max(0, _lastSeen.Length - 2);
                Segments.Add(new PathSegment(CurrentPhase, points[_offset..]));
                _breakPending = false;
            }
            else
            {
                Segments[^1] = Segments[^1] with { Points = points[_offset..] };
            }
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
        Segments.Add(new PathSegment(CurrentPhase, points));
        _lastSeen = points;
        _offset = 0;
        _breakPending = false;
    }

    private static bool IsPrefix(int[] prefix, int[] full)
    {
        if (prefix.Length > full.Length) return false;
        return prefix.AsSpan().SequenceEqual(full.AsSpan(0, prefix.Length));
    }

    // ---- persistence --------------------------------------------------------------

    private sealed record Stored(int V, List<StoredSegment> Segments);
    private sealed record StoredSegment(int Phase, int[] Points);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public byte[] ToGzipJson() =>
        GzipUtil.Compress(JsonSerializer.SerializeToUtf8Bytes(new Stored(2, Segments.Select(s => new StoredSegment(s.Phase, s.Points)).ToList()), Json));

    public static PathAccumulator FromGzipJson(byte[] gzip)
    {
        var acc = new PathAccumulator();
        if (gzip.Length == 0) return acc;
        var stored = JsonSerializer.Deserialize<Stored>(GzipUtil.Decompress(gzip), Json);
        if (stored is not null)
            acc.Segments.AddRange(stored.Segments.Where(s => s.Points.Length >= 2).Select(s => new PathSegment(s.Phase, s.Points)));

        if (acc.Segments.Count > 0)
        {
            acc._lastSeen = acc.Segments[^1].Points;
            acc.CurrentPhase = acc.Segments[^1].Phase;
            acc._grew = true;
        }
        return acc;
    }
}
