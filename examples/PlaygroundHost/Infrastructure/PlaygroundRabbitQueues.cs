using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace PlaygroundHost.Infrastructure;

/// <summary>Clears stale RabbitMQ messages before a playground scenario run (concurrent-safe on shared hosts).</summary>
public static class PlaygroundRabbitQueues
{
    public static async Task PurgeMainAndRetryAsync(
        string rabbitConnectionString,
        string mainQueueName,
        CancellationToken cancellationToken = default
    )
    {
        var factory = new ConnectionFactory { Uri = new Uri(rabbitConnectionString) };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: cancellationToken
        );

        await SafePurgeAsync(channel, mainQueueName, cancellationToken);
        await SafePurgeAsync(
            channel,
            PlaygroundAmqpNames.RetryQueueName(mainQueueName),
            cancellationToken
        );
    }

    private static async Task SafePurgeAsync(
        IChannel channel,
        string queueName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await channel.QueuePurgeAsync(queueName, cancellationToken);
        }
        catch (OperationInterruptedException)
        {
            // Queue not declared yet on this broker; nothing to purge.
        }
    }
}
