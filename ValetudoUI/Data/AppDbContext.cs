using Microsoft.EntityFrameworkCore;

namespace ValetudoUI.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CleaningRun> Runs => Set<CleaningRun>();
    public DbSet<CleaningRunMap> RunMaps => Set<CleaningRunMap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CleaningRun>(e =>
        {
            e.HasIndex(r => r.StartedUtc);
            e.HasIndex(r => r.State);
            e.Property(r => r.State).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.RunType).HasMaxLength(20);
            e.HasOne(r => r.Map)
                .WithOne(m => m.Run)
                .HasForeignKey<CleaningRunMap>(m => m.CleaningRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CleaningRunMap>(e => e.HasKey(m => m.CleaningRunId));
    }
}
