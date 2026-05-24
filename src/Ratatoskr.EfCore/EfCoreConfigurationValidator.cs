using Ratatoskr.Core;

namespace Ratatoskr.EfCore;

/// <summary>
/// Validates that consume channels receiving messages from the EF Core transport
/// have inbox configured, since the EF Core transport requires inbox for delivery.
/// </summary>
internal static class EfCoreConfigurationValidator
{
    public static void Validate(ChannelRegistry channelRegistry)
    {
        // Collect all message types published via the EF Core transport
        var efCorePublishedTypes = new HashSet<string>();
        foreach (var publishChannel in channelRegistry.GetPublishChannels())
        {
            if (!publishChannel.Transports.Contains(EfCoreTransportConstants.TransportName))
            {
                continue;
            }

            foreach (var message in publishChannel.Messages)
            {
                efCorePublishedTypes.Add(message.MessageTypeName);
            }
        }

        if (efCorePublishedTypes.Count == 0)
        {
            return;
        }

        // Check all consume channels that consume EF Core-published types
        foreach (var consumeChannel in channelRegistry.GetConsumeChannels())
        {
            var inboxConfig = consumeChannel.GetExtension<ChannelInboxConfig>();

            foreach (var message in consumeChannel.Messages)
            {
                if (!efCorePublishedTypes.Contains(message.MessageTypeName))
                {
                    continue;
                }

                if (inboxConfig == null)
                {
                    throw new InvalidOperationException(
                        $"Channel '{consumeChannel.ChannelName}' receives messages of type '{message.MessageTypeName}' "
                            + $"from the EF Core transport but does not have UseInbox<TDbContext>() configured. "
                            + $"The EF Core transport requires inbox for message delivery."
                    );
                }
            }
        }
    }
}
