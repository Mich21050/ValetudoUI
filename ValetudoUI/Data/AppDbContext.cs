using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ValetudoUI.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CleaningRun> Runs => Set<CleaningRun>();
    public DbSet<CleaningRunMap> RunMaps => Set<CleaningRunMap>();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CleaningRun>(e =>
        {
            e.HasIndex(r => r.StartedUtc);
            e.HasIndex(r => r.State);
            e.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.RunType).HasMaxLength(20);
            e.Property(r => r.Phases)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<RunPhase>>(v, JsonOptions) ?? new List<RunPhase>(),
                    new ValueComparer<List<RunPhase>>(
                        (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
                        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
                        v => JsonSerializer.Deserialize<List<RunPhase>>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions) ?? new List<RunPhase>()))
                .HasColumnType("TEXT");
            e.HasOne(r => r.Map)
                .WithOne(m => m.Run)
                .HasForeignKey<CleaningRunMap>(m => m.CleaningRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CleaningRunMap>(e => e.HasKey(m => m.CleaningRunId));
    }
}
