using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the inbox pattern.
/// </summary>
public class InboxBuilder<TDbContext> where TDbContext : DbContext, IInboxDbContext
{
    internal InboxOptions Options { get; } = new();

    internal InboxBuilder() { }

    /// <summary>
    /// Sets the polling interval for checking the database for pending handler deliveries.
    /// </summary>
    public InboxBuilder<TDbContext> WithPollingInterval(TimeSpan interval)
    {
        Options.PollingInterval = interval;
        return this;
    }

    /// <summary>
    /// Sets the number of handler statuses to process in each batch.
    /// </summary>
    public InboxBuilder<TDbContext> WithBatchSize(int batchSize)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive");
        Options.BatchSize = batchSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of delivery attempts before marking a handler as poisoned.
    /// </summary>
    public InboxBuilder<TDbContext> WithMaxRetries(int maxRetries)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries cannot be negative");
        Options.MaxRetries = maxRetries;
        return this;
    }

    /// <summary>
    /// Sets the maximum backoff delay between retry attempts.
    /// </summary>
    public InboxBuilder<TDbContext> WithMaxRetryDelay(TimeSpan delay)
    {
        Options.MaxRetryDelay = delay;
        return this;
    }

    /// <summary>
    /// Configures all options via an action.
    /// </summary>
    public InboxBuilder<TDbContext> Configure(Action<InboxOptions> configure)
    {
        configure(Options);
        return this;
    }
}
