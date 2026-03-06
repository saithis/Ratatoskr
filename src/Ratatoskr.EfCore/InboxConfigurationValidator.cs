using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore;

/// <summary>
/// Validates inbox configuration at build time, catching misconfigurations early with clear messages.
/// Follows the same pattern as <c>RabbitMqConfigurationValidator</c> in Ratatoskr.RabbitMq.
/// </summary>
internal static class InboxConfigurationValidator
{
    public static void Validate(ChannelRegistry channelRegistry, ChannelHandlerRegistry handlerRegistry)
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
                    $"Channel '{channel.ChannelName}' has inbox handler '{firstHandler.InboxKey}' " +
                    $"but does not have UseInbox<TDbContext>() configured. " +
                    $"Either add UseInbox<TDbContext>() to the channel or use WithHandler<THandler>() without a key for fire-and-forget.");
            }

            // UseInbox channel with fire-and-forget handlers that weren't explicitly opted out
            if (inboxConfig != null)
            {
                foreach (var handler in GetAllHandlersForChannel(channel))
                {
                    if (!handler.IsInbox && !handler.IsExplicitFireAndForget)
                    {
                        throw new InvalidOperationException(
                            $"Channel '{channel.ChannelName}' has UseInbox<TDbContext>() configured, " +
                            $"but handler '{handler.HandlerType.Name}' was registered without a stable key. " +
                            $"Provide a key via WithHandler<THandler>(\"key\") for inbox processing, " +
                            $"or explicitly opt out with WithHandler<THandler>(\"key\", h => h.WithoutInbox()).");
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
                    $"Inbox handler for '{handler.HandlerType.Name}' has an empty stable key. " +
                    $"Provide a non-empty key via Consumes<TMsg>(m => m.WithHandler<THandler>(\"key\")).");
        }
    }

    private static IEnumerable<ChannelHandlerRegistration> GetAllHandlersForChannel(
        ChannelRegistration channel)
    {
        foreach (var message in channel.Messages)
        {
            var handlerRegs = message.GetExtension<MessageHandlerRegistrations>();
            if (handlerRegs == null) continue;

            foreach (var handler in handlerRegs.Handlers)
                yield return handler;
        }
    }
}
