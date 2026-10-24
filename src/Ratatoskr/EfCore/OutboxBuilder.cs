using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the outbox pattern.
/// </summary>
public class OutboxBuilder<TDbContext>
    : DurableProcessingBuilder<OutboxBuilder<TDbContext>, OutboxOptions>
    where TDbContext : DbContext
{
    internal OutboxBuilder() { }

    /// <summary>
    /// Sets the maximum time a send operation is allowed to run before being cancelled.
    /// Timeout cancellation is treated as a failure and increments the error count.
    /// </summary>
    public OutboxBuilder<TDbContext> WithSendTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Options.SendTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the maximum allowed size of the serialized message body in bytes.
    /// Messages exceeding this limit will cause SaveChangesAsync to throw.
    /// </summary>
    public OutboxBuilder<TDbContext> WithMaxMessageSize(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0);
        Options.MaxMessageSize = maxBytes;
        return this;
    }
}
