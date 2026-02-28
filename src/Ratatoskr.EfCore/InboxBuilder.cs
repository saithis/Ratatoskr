using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the inbox pattern.
/// </summary>
public class InboxBuilder<TDbContext> where TDbContext : DbContext, IInboxDbContext
{
    internal InboxOptions Options { get; } = new();
    internal bool RegisterBackgroundService { get; private set; } = true;

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
    /// Enrolls all handlers (that have not explicitly called <c>WithoutInbox()</c>) in the inbox by default.
    /// Handlers without a stable key use the handler's CLR full name as the stable key.
    /// </summary>
    public InboxBuilder<TDbContext> WithDefaultInboxEnabled()
    {
        Options.DefaultHandlerInboxEnabled = true;
        return this;
    }

    /// <summary>
    /// Prevents the <see cref="InboxProcessor{TDbContext}"/> from being registered as a hosted service.
    /// Use this in integration tests where you want deterministic control over when inbox processing runs
    /// (e.g. by calling <c>InboxMessageProcessor.ProcessBatchAsync</c> directly).
    /// </summary>
    public InboxBuilder<TDbContext> WithoutBackgroundProcessing()
    {
        RegisterBackgroundService = false;
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
