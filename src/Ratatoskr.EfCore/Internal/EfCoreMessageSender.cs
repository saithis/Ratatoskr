using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Sends messages by writing directly to the target inbox tables via <see cref="IEfCoreInboxAcceptor"/>.
/// Used by the outbox processor for cross-DbContext delivery and by DirectPublishAsync.
/// </summary>
internal partial class EfCoreMessageSender(
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
                // matching consume channel. With no consume channel for this type there is
                // nowhere to deliver: the send is a no-op and the outbox row (if any) is marked
                // processed. That is intended for fan-out-to-nobody, but it also silently hides a
                // common misconfiguration (a producer routed to EF Core with no matching consumer),
                // so surface it as a warning rather than dropping the message without a trace.
                EfCoreMessageSenderLog.NoConsumeChannelForType(logger, props.Type);
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

internal static partial class EfCoreMessageSenderLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Message of type '{Type}' was routed to the EF Core transport but no consume channel is registered for it. The message will not be delivered to any inbox. Register a consume channel with UseInbox<TDbContext>() for this type, or remove the EF Core transport from its producer."
    )]
    public static partial void NoConsumeChannelForType(ILogger logger, string type);
}
