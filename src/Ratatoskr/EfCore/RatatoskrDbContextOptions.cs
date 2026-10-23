using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Configuration options for Ratatoskr durability (inbox and outbox) on a specific DbContext.
/// </summary>
public class RatatoskrDbContextOptions<TDbContext>
    where TDbContext : DbContext, IRatatoskrDbContext
{
    /// <summary>
    /// Outbox options (polling interval, batch size, retry policy, retention).
    /// </summary>
    public OutboxOptions Outbox { get; } = new();

    /// <summary>
    /// Inbox options (polling interval, batch size, retry policy, retention).
    /// </summary>
    public InboxOptions Inbox { get; } = new();

    /// <summary>
    /// Sets how often background queries refresh backlog gauge state. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan MetricsPollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sets the cancellation timeout for each individual COUNT query used to update backlog gauges. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan MetricsQueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether to register background hosted services for processing outbox and inbox. Defaults to true.
    /// </summary>
    public bool RegisterBackgroundServices { get; set; } = true;

    /// <summary>
    /// Disables background processing for both inbox and outbox (useful in integration test setups).
    /// </summary>
    public RatatoskrDbContextOptions<TDbContext> WithoutBackgroundProcessing()
    {
        RegisterBackgroundServices = false;
        return this;
    }
}
