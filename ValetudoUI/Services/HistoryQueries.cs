using Microsoft.EntityFrameworkCore;
using ValetudoUI.Data;

namespace ValetudoUI.Services;

public sealed record RunListItem(
    int Id,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    RunState State,
    string RunType,
    int? DurationSeconds,
    long? AreaCm2,
    List<string> SegmentNames,
    string? EndStatus,
    bool StartedBeforeAppStart,
    string? ErrorMessage);

/// <summary>Stored map of a run plus what is needed to decide how long a browser may cache it.</summary>
public sealed record RunMapBlob(byte[] MapJsonGzip, RunState State, DateTime LastCheckpointUtc)
{
    /// <summary>Changes whenever the stored map is rewritten (each checkpoint and at completion).</summary>
    public string Version => LastCheckpointUtc.Ticks.ToString();
}

public sealed class HistoryQueries(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<RunListItem>> ListAsync(int skip, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking()
            .OrderByDescending(r => r.StartedUtc)
            .Skip(skip).Take(take)
            .Select(r => new RunListItem(r.Id, r.StartedUtc, r.EndedUtc, r.State, r.RunType, r.DurationSeconds, r.AreaCm2,
                r.SegmentNames, r.EndStatus, r.StartedBeforeAppStart, r.ErrorMessage))
            .ToListAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Runs.CountAsync(ct);
    }

    public async Task<CleaningRun?> GetAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<RunMapBlob?> GetMapGzipAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RunMaps.AsNoTracking()
            .Where(m => m.CleaningRunId == id)
            .Select(m => new RunMapBlob(m.MapJsonGzip, m.Run.State, m.Run.LastCheckpointUtc))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.RunMaps.Where(m => m.CleaningRunId == id).ExecuteDeleteAsync(ct);
        return await db.Runs.Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0;
    }
}
