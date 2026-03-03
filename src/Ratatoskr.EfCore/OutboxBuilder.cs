using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the outbox pattern.
/// </summary>
public class OutboxBuilder<TDbContext> where TDbContext : DbContext, IOutboxDbContext
{
    internal IServiceCollection Services { get; }
    internal OutboxOptions Options { get; } = new();
    
    internal OutboxBuilder(IServiceCollection services)
    {
        Services = services;
    }
    
    /// <summary>
    /// Sets the polling interval for checking the database.
    /// </summary>
    public OutboxBuilder<TDbContext> WithPollingInterval(TimeSpan interval)
    {
        Options.PollingInterval = interval;
        return this;
    }
    
    /// <summary>
    /// Sets the batch size for processing messages.
    /// </summary>
    public OutboxBuilder<TDbContext> WithBatchSize(int batchSize)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive");
        Options.BatchSize = batchSize;
        return this;
    }
    
    /// <summary>
    /// Sets the maximum number of retries before a message is marked as poisoned.
    /// </summary>
    public OutboxBuilder<TDbContext> WithMaxRetries(int maxRetries)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries cannot be negative");
        Options.MaxRetries = maxRetries;
        return this;
    }
    
    /// <summary>
    /// Sets the maximum time a send operation is allowed to run before being cancelled.
    /// Timeout cancellation is treated as a failure and increments the error count.
    /// </summary>
    public OutboxBuilder<TDbContext> WithSendTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Send timeout must be positive");
        Options.SendTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets how long to keep successfully processed outbox messages before automatic cleanup.
    /// Default: 7 days. Set to null to disable cleanup of processed messages.
    /// </summary>
    public OutboxBuilder<TDbContext> WithCompletedRetention(TimeSpan? retention)
    {
        if (retention.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention.Value, TimeSpan.Zero, nameof(retention));
        Options.CompletedRetention = retention;
        return this;
    }

    /// <summary>
    /// Sets how long to keep poisoned outbox messages before automatic cleanup.
    /// Default: 30 days. Set to null to disable cleanup of poisoned messages.
    /// </summary>
    public OutboxBuilder<TDbContext> WithPoisonedRetention(TimeSpan? retention)
    {
        if (retention.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention.Value, TimeSpan.Zero, nameof(retention));
        Options.PoisonedRetention = retention;
        return this;
    }

    /// <summary>
    /// Sets how often the cleanup processor runs.
    /// Default: 1 hour.
    /// </summary>
    public OutboxBuilder<TDbContext> WithCleanupInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(interval));
        Options.CleanupInterval = interval;
        return this;
    }

    /// <summary>
    /// Disables automatic cleanup of processed and poisoned outbox messages.
    /// </summary>
    public OutboxBuilder<TDbContext> WithoutCleanup()
    {
        Options.CompletedRetention = null;
        Options.PoisonedRetention = null;
        return this;
    }

    /// <summary>
    /// Configures all options via an action.
    /// </summary>
    public OutboxBuilder<TDbContext> Configure(Action<OutboxOptions> configure)
    {
        configure(Options);
        return this;
    }
}
