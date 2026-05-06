using Microsoft.EntityFrameworkCore;

namespace PlaygroundHost.Persistence;

public class PlaygroundDbContext(DbContextOptions<PlaygroundDbContext> options) : DbContext(options)
{
    public DbSet<PlaygroundRunEntity> Runs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PlaygroundRunEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ScenarioSlug).HasMaxLength(256);
            e.Property(x => x.State).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(4000);
            e.Property(x => x.CurrentStep).HasMaxLength(256);
        });
    }
}
