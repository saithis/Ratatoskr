using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Immutable snapshot of backlog counts for one DbContext (one scrape reads a consistent tuple).
/// </summary>
internal readonly record struct DbContextMetrics(
    long PendingOutboxCount,
    long PoisonedOutboxCount,
    long PendingInboxCount,
    long PoisonedInboxCount
);

internal class EfCoreMetricsState
{
    /// <summary>
    /// Tracks metric state per DbContext type full name (see <see cref="Type.FullName"/>).
    /// </summary>
    public ConcurrentDictionary<string, DbContextMetrics> ContextMetrics { get; } =
        new(StringComparer.Ordinal);

    public bool TryGetValue<TDbContext>(TDbContext _, out DbContextMetrics metrics)
        where TDbContext : DbContext
    {
        var type = typeof(TDbContext);
        return TryGetValue(type, out metrics);
    }

    public bool TryGetValue(Type dbContextType, out DbContextMetrics metrics)
    {
        return ContextMetrics.TryGetValue(
            dbContextType.FullName ?? dbContextType.Name,
            out metrics
        );
    }
}
