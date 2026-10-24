using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore;

/// <summary>
/// Builder for configuring the inbox pattern.
/// </summary>
public class InboxBuilder<TDbContext> : DurableProcessingBuilder<InboxBuilder<TDbContext>, InboxOptions>
    where TDbContext : DbContext
{
    internal InboxBuilder() { }

    /// <summary>
    /// Requires consume channels to use inbox according to the selected policy.
    /// </summary>
    public InboxBuilder<TDbContext> WithConsumeChannelInboxRequirement(
        ConsumeChannelInboxRequirement requirement
    )
    {
        Options.ConsumeChannelInboxRequirement = requirement;
        return this;
    }

    /// <summary>
    /// Sets the maximum time a handler is allowed to run before being cancelled.
    /// Timeout cancellation is treated as a handler failure and increments the error count.
    /// </summary>
    public InboxBuilder<TDbContext> WithHandlerTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Options.HandlerTimeout = timeout;
        return this;
    }
}
