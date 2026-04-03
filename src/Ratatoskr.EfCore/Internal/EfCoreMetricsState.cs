using System.Collections.Concurrent;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Immutable snapshot of backlog counts for one DbContext (one scrape reads a consistent tuple).
/// </summary>
internal readonly record struct DbContextMetrics(
    long PendingOutboxCount,
    long PoisonedOutboxCount,
    long PendingInboxCount,
    long PoisonedInboxCount);

internal class EfCoreMetricsState
{
    /// <summary>
    /// Tracks metric state per DbContext type full name (see <see cref="System.Type.FullName"/>).
    /// </summary>
    public ConcurrentDictionary<string, DbContextMetrics> ContextMetrics { get; } = new();
}
