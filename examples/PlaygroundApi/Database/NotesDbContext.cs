using Microsoft.EntityFrameworkCore;
using PlaygroundApi.Database.Entities;
using Ratatoskr.EfCore;

namespace PlaygroundApi.Database;

public class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public DbSet<Note> Notes { get; set; }
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddOutboxEntities(Database);
        modelBuilder.AddInboxEntities(Database);
    }
}