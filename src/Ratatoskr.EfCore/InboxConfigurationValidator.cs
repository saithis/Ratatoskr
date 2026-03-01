using Ratatoskr.Core;

namespace Ratatoskr.EfCore;

/// <summary>
/// Validates inbox configuration at build time, catching misconfigurations early with clear messages.
/// Follows the same pattern as <c>RabbitMqConfigurationValidator</c> in Ratatoskr.RabbitMq.
/// </summary>
internal static class InboxConfigurationValidator
{
    public static void Validate(ChannelRegistry channelRegistry, InboxHandlerRegistry inboxRegistry)
    {
        if (inboxRegistry.IsEmpty)
            return;

        foreach (var handler in inboxRegistry.GetAll())
        {
            if (string.IsNullOrWhiteSpace(handler.Key))
                throw new InvalidOperationException(
                    $"Inbox handler for '{handler.HandlerType.Name}' has an empty stable key. " +
                    $"Provide a non-empty key via AddHandler<TMsg, THandler>(h => h.WithInbox(\"key\")).");

            // Verify the message type is registered in at least one consume channel.
            // Without this, MessageDispatcher cannot route incoming messages to the handler.
            var hasConsumeChannel = channelRegistry.GetConsumeChannels()
                .Any(ch => ch.Messages.Any(m => m.MessageType == handler.MessageType));

            if (!hasConsumeChannel)
                throw new InvalidOperationException(
                    $"Inbox handler '{handler.Key}' handles '{handler.MessageType.Name}', " +
                    $"but that type is not registered in any consume channel. " +
                    $"Add it with .Consumes<{handler.MessageType.Name}>() on a consume channel.");
        }
    }
}
