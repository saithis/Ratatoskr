using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Sends messages by writing directly to the target inbox tables via <see cref="IEfCoreInboxAcceptor"/>.
/// Used by the outbox processor for cross-DbContext delivery and by DirectPublishAsync.
/// </summary>
internal class EfCoreMessageSender(
    ChannelRegistry channelRegistry,
    IEnumerable<IEfCoreInboxAcceptor> acceptors,
    ILogger<EfCoreMessageSender> logger) : IMessageSender
{
    private readonly Dictionary<Type, IEfCoreInboxAcceptor> _acceptorMap =
        acceptors.ToDictionary(a => a.DbContextType);

    public string TransportName => EfCoreTransportConstants.TransportName;

    public async Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        if (props.Type == null)
            throw new InvalidOperationException("Cannot send via EF Core transport: message has no Type.");

        var consumeChannels = channelRegistry.FindConsumeChannelsForType(props.Type);

        foreach (var (channel, _) in consumeChannels)
        {
            var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
            if (inboxConfig == null)
            {
                logger.LogWarning(
                    "Channel '{Channel}' has no inbox configured — skipping EF Core transport delivery for message '{MessageId}'",
                    channel.ChannelName, props.Id);
                continue;
            }

            if (!_acceptorMap.TryGetValue(inboxConfig.DbContextType, out var acceptor))
            {
                throw new InvalidOperationException(
                    $"No inbox acceptor registered for DbContext '{inboxConfig.DbContextType.Name}'. " +
                    $"Ensure AddEfCoreDurability<{inboxConfig.DbContextType.Name}>() with UseInbox() is configured.");
            }

            await acceptor.AcceptAsync(content, props, TransportName, channel.ChannelName, cancellationToken);
        }
    }
}
