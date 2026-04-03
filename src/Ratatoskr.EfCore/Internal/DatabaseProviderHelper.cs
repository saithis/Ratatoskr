using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Provides provider-specific SQL for filtered/partial indexes.
/// Detects the database provider via <see cref="DatabaseFacade.ProviderName"/>
/// to avoid compile-time dependencies on provider-specific packages.
/// </summary>
internal static class DatabaseProviderHelper
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    /// <summary>
    /// Returns a filter expression for the outbox processing index, or null if the provider
    /// does not support filtered indexes (e.g. SQLite, InMemory).
    /// </summary>
    public static string? GetOutboxProcessingFilter(DatabaseFacade database)
    {
        return database.ProviderName switch
        {
            PostgresProvider => "\"ProcessedAt\" IS NULL AND \"IsPoisoned\" = false",
            SqlServerProvider => "[ProcessedAt] IS NULL AND [IsPoisoned] = 0",
            _ => null
        };
    }

    /// <summary>
    /// Returns a filter expression for the inbox handler status processing index, or null if the provider
    /// does not support filtered indexes.
    /// </summary>
    public static string? GetInboxProcessingFilter(DatabaseFacade database)
    {
        return database.ProviderName switch
        {
            PostgresProvider => "\"CompletedAt\" IS NULL AND \"IsPoisoned\" = false",
            SqlServerProvider => "[CompletedAt] IS NULL AND [IsPoisoned] = 0",
            _ => null
        };
    }
}
