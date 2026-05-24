using Microsoft.EntityFrameworkCore;
using Ratatoskr.Config;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods to enable the inbox pattern for durable, per-handler message delivery.
/// </summary>
public static class InboxPublicApiExtensions
{
    /// <summary>
    /// Explicitly opts this consume channel out of the optional consume-channel inbox requirement policy.
    /// This does not bypass transport-specific requirements (for example, EF Core transport still requires inbox).
    /// </summary>
    public static ConsumeChannelBuilder AllowConsumeWithoutInbox(this ConsumeChannelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Channel.SetExtension(new ConsumeChannelInboxRequirementOptOut());
        return builder;
    }

    /// <summary>
    /// Enables the inbox pattern on this consume channel.
    /// All handlers registered with a stable key on this channel will be inbox-managed.
    /// Requires <c>AddEfCoreDurability&lt;TDbContext&gt;(d =&gt; d.UseInbox())</c> to be called on the bus builder.
    /// </summary>
    public static ConsumeChannelBuilder UseInbox<TDbContext>(this ConsumeChannelBuilder builder)
        where TDbContext : DbContext, IInboxDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Channel.SetExtension(new ChannelInboxConfig(typeof(TDbContext)));

        // Deferred validation: ensure AddEfCoreDurability<TDbContext>(d => d.UseInbox()) was called
        var services = builder.Services;
        var channelName = builder.Channel.ChannelName;
        builder.RatatoskrBuilder.AddValidator(_ =>
        {
            if (!services.Any(d => d.ServiceType == typeof(InboxOptionsHolder<TDbContext>)))
            {
                throw new InvalidOperationException(
                    $"Channel '{channelName}' uses UseInbox<{typeof(TDbContext).Name}>() "
                        + $"but AddEfCoreDurability<{typeof(TDbContext).Name}>(d => d.UseInbox()) was not configured. "
                        + $"Call AddEfCoreDurability before configuring consume channels."
                );
            }
        });

        return builder;
    }
}
