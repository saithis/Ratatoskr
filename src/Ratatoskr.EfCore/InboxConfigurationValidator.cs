using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Validates inbox configuration at build time, catching misconfigurations early with clear messages.
/// Follows the same pattern as <c>RabbitMqConfigurationValidator</c> in Ratatoskr.RabbitMq.
/// </summary>
internal static class InboxConfigurationValidator
{
    public static void Validate(
        ChannelRegistry channelRegistry,
        ChannelHandlerRegistry handlerRegistry,
        ConsumeChannelInboxPolicyAggregator? policyAggregator = null
    )
    {
        foreach (var channel in channelRegistry.GetConsumeChannels())
        {
            var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
            var inboxHandlers = handlerRegistry.GetInboxHandlers(channel.ChannelName);

            // Inbox handlers on a channel without UseInbox<>()
            if (inboxConfig == null && inboxHandlers.Count > 0)
            {
                var firstHandler = inboxHandlers[0];
                throw new InvalidOperationException(
                    $"Channel '{channel.ChannelName}' has inbox handler '{firstHandler.InboxKey}' "
                        + $"but does not have UseInbox<TDbContext>() configured. "
                        + $"Either add UseInbox<TDbContext>() to the channel or move fire-and-forget handlers to a separate channel without UseInbox."
                );
            }

            if (inboxConfig == null && inboxHandlers.Count == 0)
                ValidateChannelInboxRequirement(channel, policyAggregator);

            // UseInbox channel must not have fire-and-forget handlers
            if (inboxConfig != null)
            {
                foreach (var handler in GetAllHandlersForChannel(channel))
                {
                    if (!handler.IsInbox)
                    {
                        throw new InvalidOperationException(
                            $"Channel '{channel.ChannelName}' has UseInbox<TDbContext>() configured, "
                                + $"but handler '{handler.HandlerType.Name}' was registered without a stable key. "
                                + $"Provide a key via WithHandler<THandler>(\"key\") for inbox processing, "
                                + $"or move fire-and-forget handlers to a separate channel without UseInbox."
                        );
                    }
                }
            }
        }

        if (handlerRegistry.HasNoInboxHandlers)
            return;

        foreach (var handler in handlerRegistry.GetAllInboxHandlers())
        {
            if (string.IsNullOrWhiteSpace(handler.InboxKey))
                throw new InvalidOperationException(
                    $"Inbox handler for '{handler.HandlerType.Name}' has an empty stable key. "
                        + $"Provide a non-empty key via Consumes<TMsg>(m => m.WithHandler<THandler>(\"key\"))."
                );
        }
    }

    private static void ValidateChannelInboxRequirement(
        ChannelRegistration channel,
        ConsumeChannelInboxPolicyAggregator? policyAggregator
    )
    {
        if (
            policyAggregator == null
            || policyAggregator.EffectiveRequirement == ConsumeChannelInboxRequirement.None
        )
            return;

        var isOptedOut = channel.GetExtension<ConsumeChannelInboxRequirementOptOut>() != null;
        if (isOptedOut)
            return;

        var message =
            $"Channel '{channel.ChannelName}' does not have UseInbox<TDbContext>() configured. "
            + $"Either add UseInbox<TDbContext>() to the channel or explicitly opt out with AllowConsumeWithoutInbox().";

        if (policyAggregator.EffectiveRequirement == ConsumeChannelInboxRequirement.Fail)
            throw new InvalidOperationException(message);

        policyAggregator.AddWarning(message);
    }

    private static IEnumerable<ChannelHandlerRegistration> GetAllHandlersForChannel(
        ChannelRegistration channel
    )
    {
        foreach (var message in channel.Messages)
        {
            var handlerRegs = message.GetExtension<MessageHandlerRegistrations>();
            if (handlerRegs == null)
                continue;

            foreach (var handler in handlerRegs.Handlers)
                yield return handler;
        }
    }
}
