using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the outbox pattern.
/// </summary>
public class OutboxBuilder<TDbContext> where TDbContext : DbContext
{
    internal OutboxOptions Options { get; } = new();

    internal OutboxBuilder() { }
    
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
    /// Configures all options via an action.
    /// </summary>
    public OutboxBuilder<TDbContext> Configure(Action<OutboxOptions> configure)
    {
        configure(Options);
        return this;
    }
}
