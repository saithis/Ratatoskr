using System.Collections.Concurrent;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Holds metrics gathered periodically from the database.
/// This prevents measuring queries from heavily impacting the DB on rapid gauge pulls.
/// </summary>
internal class DbContextMetrics
{
    public long PendingOutboxCount { get; set; }
    public long PoisonedOutboxCount { get; set; }
    public long PendingInboxCount { get; set; }
    public long PoisonedInboxCount { get; set; }
}

internal class EfCoreMetricsState
{
    /// <summary>
    /// Tracks metric state per DbContext type name.
    /// </summary>
    public ConcurrentDictionary<string, DbContextMetrics> ContextMetrics { get; } = new();
}
