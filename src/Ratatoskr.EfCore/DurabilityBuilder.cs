using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring EF Core durability (inbox and/or outbox) for a specific DbContext type.
/// </summary>
public class DurabilityBuilder<TDbContext>
    where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
{
    internal InboxBuilder<TDbContext>? InboxBuilder { get; private set; }
    internal OutboxBuilder<TDbContext>? OutboxBuilder { get; private set; }

    internal TimeSpan MetricsPollingInterval { get; private set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan MetricsQueryTimeout { get; private set; } = TimeSpan.FromSeconds(5);

    internal DurabilityBuilder() { }

    /// <summary>
    /// Sets how often background queries refresh backlog gauge state. Defaults to 30 seconds.
    /// </summary>
    public DurabilityBuilder<TDbContext> WithMetricsPollingInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        MetricsPollingInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the cancellation timeout for each individual COUNT query used to update backlog gauges.
    /// Defaults to 5 seconds per query.
    /// </summary>
    public DurabilityBuilder<TDbContext> WithMetricsQueryTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        MetricsQueryTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Enables the inbox pattern for this DbContext with default options.
    /// </summary>
    public DurabilityBuilder<TDbContext> UseInbox()
    {
        return UseInbox(configure: null);
    }

    /// <summary>
    /// Enables the inbox pattern for this DbContext with custom options.
    /// </summary>
    public DurabilityBuilder<TDbContext> UseInbox(Action<InboxBuilder<TDbContext>>? configure)
    {
        InboxBuilder = new InboxBuilder<TDbContext>();
        configure?.Invoke(InboxBuilder);
        return this;
    }

    /// <summary>
    /// Enables the outbox pattern for this DbContext with default options.
    /// </summary>
    public DurabilityBuilder<TDbContext> UseOutbox()
    {
        return UseOutbox(configure: null);
    }

    /// <summary>
    /// Enables the outbox pattern for this DbContext with custom options.
    /// </summary>
    public DurabilityBuilder<TDbContext> UseOutbox(Action<OutboxBuilder<TDbContext>>? configure)
    {
        OutboxBuilder = new OutboxBuilder<TDbContext>();
        configure?.Invoke(OutboxBuilder);
        return this;
    }
}

/// <summary>
/// Sentinel type for idempotency detection of <c>AddEfCoreDurability&lt;TDbContext&gt;</c>.
/// </summary>
internal sealed class DurabilityMarker<TDbContext>;
