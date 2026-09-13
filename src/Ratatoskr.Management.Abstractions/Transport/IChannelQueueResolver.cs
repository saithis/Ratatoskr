namespace Ratatoskr.Management.Contracts;

/// <summary>
/// Resolves live queue depths and Dead Letter Queue information for a channel registration.
/// Implemented by transports like RabbitMQ to enrich topology views.
/// </summary>
public interface IChannelQueueResolver
{
    /// <summary>
    /// Resolves the physical queues and DLQ counts associated with the channel.
    /// </summary>
    Task<IReadOnlyList<QueueTopology>> ResolveQueuesAsync(
        string channelName,
        ChannelIntent intent,
        IReadOnlyList<string> messageTypes,
        CancellationToken cancellationToken = default
    );
}
