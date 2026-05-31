using System.Diagnostics;
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
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<EfCoreMessageSender> logger
) : IMessageSender
{
    private readonly Dictionary<Type, IEfCoreInboxAcceptor> _acceptorMap = BuildAcceptorMap(
        acceptors
    );

    public string TransportName => EfCoreTransportConstants.TransportName;

    public async Task SendAsync(
        byte[] content,
        MessageProperties props,
        CancellationToken cancellationToken
    )
    {
        if (props.Type == null)
        {
            throw new InvalidOperationException(
                "Cannot send via EF Core transport: message has no Type."
            );
        }

        using var activity = EfCoreTelemetry.StartSendActivity(props, content.Length);
        var startTimestamp = Stopwatch.GetTimestamp();
        Exception? sendException = null;

        try
        {
            var consumeChannels = channelRegistry.FindConsumeChannelsForType(props.Type).ToList();

            if (consumeChannels.Count == 0)
            {
                // The EF Core transport delivers in-process by writing to the inbox of each
                // matching consume channel. With no consume channel for this type the message
                // would reach no inbox; completing silently here would let the outbox mark the
                // row processed and the message would be lost without a trace. Fail instead so the
                // misconfiguration surfaces (direct publish throws to the caller; the outbox retries
                // and then poisons the row, keeping it visible).
                throw new InvalidOperationException(
                    $"Cannot deliver message of type '{props.Type}' via the EF Core transport: "
                        + "no consume channel is registered for this message type. The EF Core "
                        + "transport delivers in-process through the inbox, so the application must "
                        + "register a consume channel with UseInbox<TDbContext>() for this type "
                        + "(or remove the EF Core transport from its producer)."
                );
            }

            foreach (var (channel, _) in consumeChannels)
            {
                var inboxConfig =
                    channel.GetExtension<ChannelInboxConfig>()
                    ?? throw new InvalidOperationException(
                        $"Channel '{channel.ChannelName}' has no inbox configured. "
                            + "The EF Core transport requires UseInbox<TDbContext>() on all consume channels. "
                            + "Either add UseInbox<TDbContext>() or use a different transport."
                    );
                if (!_acceptorMap.TryGetValue(inboxConfig.DbContextType, out var acceptor))
                {
                    throw new InvalidOperationException(
                        $"No inbox acceptor registered for DbContext '{inboxConfig.DbContextType.Name}'. "
                            + $"Ensure AddEfCoreDurability<{inboxConfig.DbContextType.Name}>() with UseInbox() is configured."
                    );
                }

                await acceptor.AcceptAsync(
                    content,
                    props,
                    TransportName,
                    channel.ChannelName,
                    cancellationToken
                );
            }
        }
        catch (Exception ex)
        {
            sendException = ex;
            EfCoreTelemetry.SetActivityError(activity, ex);
            throw;
        }
        finally
        {
            EfCoreTelemetry.RecordSent(startTimestamp, sendException);

            await observers.NotifyAsync(
                new MessageActivity
                {
                    Stage = MessageStage.Sent,
                    Properties = props,
                    SerializedBody = content,
                    TransportName = TransportName,
                    Exception = sendException,
                    Timestamp = timeProvider.GetUtcNow(),
                },
                logger
            );
        }
    }

    private static Dictionary<Type, IEfCoreInboxAcceptor> BuildAcceptorMap(
        IEnumerable<IEfCoreInboxAcceptor> acceptors
    )
    {
        var map = new Dictionary<Type, IEfCoreInboxAcceptor>();
        foreach (var acceptor in acceptors)
        {
            if (!map.TryAdd(acceptor.DbContextType, acceptor))
            {
                throw new InvalidOperationException(
                    $"Duplicate IEfCoreInboxAcceptor registered for DbContext '{acceptor.DbContextType.Name}'. "
                        + $"Ensure AddEfCoreDurability<{acceptor.DbContextType.Name}>() is only called once."
                );
            }
        }
        return map;
    }
}
