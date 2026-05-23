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
    EfCoreTelemetry telemetry,
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
            throw new InvalidOperationException(
                "Cannot send via EF Core transport: message has no Type."
            );

        using var activity = telemetry.StartSendActivity(props, content.Length);
        var startTimestamp = Stopwatch.GetTimestamp();
        Exception? sendException = null;

        try
        {
            var consumeChannels = channelRegistry.FindConsumeChannelsForType(props.Type);

            foreach (var (channel, _) in consumeChannels)
            {
                var inboxConfig = channel.GetExtension<ChannelInboxConfig>();
                if (inboxConfig == null)
                {
                    throw new InvalidOperationException(
                        $"Channel '{channel.ChannelName}' has no inbox configured. "
                            + $"The EF Core transport requires UseInbox<TDbContext>() on all consume channels. "
                            + $"Either add UseInbox<TDbContext>() or use a different transport."
                    );
                }

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
            telemetry.RecordSent(startTimestamp, sendException);

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
