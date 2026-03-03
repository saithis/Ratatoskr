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
        InboxMessageRegistry inboxMessageRegistry,
        InboxHandlerRegistry inboxHandlerRegistry)
    {
        if (inboxMessageRegistry.IsEmpty)
            return;

        // Validate that every inbox-managed message type has at least one handler registered.
        foreach (var channelName in inboxMessageRegistry.GetChannelNames())
        {
            foreach (var wireTypeName in inboxMessageRegistry.GetWireTypeNames(channelName))
            {
                var handlers = inboxHandlerRegistry.GetByWireTypeName(wireTypeName);
                if (handlers.Count == 0)
                    throw new InvalidOperationException(
                        $"Message type '{wireTypeName}' on channel '{channelName}' is configured for inbox " +
                        $"(UseInbox()), but no handlers are registered for it. " +
                        $"Add at least one handler via AddHandler<TMsg, THandler>().");
            }
        }

        // Validate that all handler keys are non-empty (should always be true with auto-generated keys).
        foreach (var handler in inboxHandlerRegistry.GetAll())
        {
            if (string.IsNullOrWhiteSpace(handler.Key))
                throw new InvalidOperationException(
                    $"Inbox handler for '{handler.HandlerType.Name}' has an empty stable key. " +
                    $"This is an internal error — handler keys should be auto-generated from the handler type name.");
        }
    }
}
