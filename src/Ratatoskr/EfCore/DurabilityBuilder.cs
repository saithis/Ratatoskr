using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring EF Core durability (inbox and/or outbox) for a specific DbContext type.
/// </summary>
public class DurabilityBuilder<TDbContext>
    where TDbContext : DbContext, IRatatoskrDbContext
{
    internal InboxBuilder<TDbContext>? InboxBuilder { get; private set; }
    internal OutboxBuilder<TDbContext>? OutboxBuilder { get; private set; }

    internal TimeSpan MetricsPollingInterval { get; private set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan MetricsQueryTimeout { get; private set; } = TimeSpan.FromSeconds(5);
    internal bool IsDefault { get; private set; }
    internal bool RegisterBackgroundServices { get; private set; } = true;

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
    /// Makes this DbContext the default, used by <c>IRatatoskr.PublishAsync</c> without a DbContext type
    /// argument and by consume channels with keyed handlers that do not name a DbContext.
    /// Without this, the first registered DbContext is the default. Only one DbContext may be marked as default.
    /// </summary>
    public DurabilityBuilder<TDbContext> AsDefault()
    {
        IsDefault = true;
        return this;
    }

    /// <summary>
    /// Disables all background services for this DbContext: inbox and outbox processing, cleanup
    /// and backlog metrics refresh (useful in integration test setups).
    /// </summary>
    public DurabilityBuilder<TDbContext> WithoutBackgroundProcessing()
    {
        RegisterBackgroundServices = false;
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
// ReSharper disable once UnusedTypeParameter
internal sealed class DurabilityMarker<TDbContext>;

/// <summary>
/// Sentinel type marking that the EF Core services shared by all DbContexts are registered.
/// </summary>
internal sealed class EfCoreSharedServicesMarker;
