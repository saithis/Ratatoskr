using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods for registering Ratatoskr persistence in the EF Core model.
/// </summary>
public static class RatatoskrEfCoreModelExtensions
{
    /// <summary>
    /// Adds all Entity Framework Core model configuration required for Ratatoskr durability (inbox and outbox tables, indexes, and constraints).
    /// Future Ratatoskr EF features will be included here so applications do not need to call multiple setup methods.
    /// </summary>
    /// <param name="modelBuilder">The model builder (typically from <c>OnModelCreating</c>).</param>
    /// <param name="database">
    /// The context's <see cref="DbContext.Database"/> so provider-specific filtered indexes can be applied for supported providers
    /// (PostgreSQL, SQL Server), avoiding full-table processing indexes on large tables.
    /// </param>
    public static void AddRatatoskrEfCoreModel(
        this ModelBuilder modelBuilder,
        DatabaseFacade database
    )
    {
        ArgumentNullException.ThrowIfNull(database);

        RatatoskrEntityModelConfiguration.ConfigureOutboxEntities(modelBuilder, database);
        RatatoskrEntityModelConfiguration.ConfigureInboxEntities(modelBuilder, database);
    }
}
