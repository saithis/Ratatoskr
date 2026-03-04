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
        InboxRoutingTable routingTable,
        InboxHandlerRegistry inboxHandlerRegistry)
    {
        if (routingTable.IsEmpty)
            return;

        // Validate that every inbox-managed message type has at least one handler registered.
        foreach (var channelName in routingTable.GetChannelNames())
        {
            foreach (var wireTypeName in routingTable.GetWireTypeNames(channelName))
            {
                var handlers = inboxHandlerRegistry.GetByWireTypeName(wireTypeName);
                if (handlers.Count == 0)
                    throw new InvalidOperationException(
                        $"Message type '{wireTypeName}' on channel '{channelName}' is configured for inbox " +
                        $"(UseInbox()), but no inbox-eligible handlers are registered for it. " +
                        $"Add at least one handler via AddHandler<TMsg, THandler>(). " +
                        $"Note: singleton handler instances (AddHandler(instance)) are not eligible for inbox management.");
            }
        }

        // Validate that all handler keys are non-empty.
        foreach (var handler in inboxHandlerRegistry.GetAll())
        {
            if (string.IsNullOrWhiteSpace(handler.Key))
                throw new InvalidOperationException(
                    $"Inbox handler '{handler.HandlerType.Name}' has an empty stable key. " +
                    $"Add [HandlerKey(\"...\")] to the handler class or pass a key to AddHandler(\"...\").");
        }

        // Validate that UseInbox<T>() on a channel is not called more than once.
        // (This is enforced at config time by SetExtension overwriting, but we also check
        // that every inbox-managed channel has a mapped DbContext type.)
        foreach (var channelName in routingTable.GetChannelNames())
        {
            if (routingTable.GetDbContextType(channelName) == null)
                throw new InvalidOperationException(
                    $"Channel '{channelName}' has inbox-managed messages but no DbContext mapping. " +
                    $"This is an internal error — the deferred action should have set this up.");
        }
    }
}
