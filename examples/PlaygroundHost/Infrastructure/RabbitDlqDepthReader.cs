using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace PlaygroundHost.Infrastructure;

public static class RabbitDlqDepthReader
{
    public static async Task<uint> GetDlqCountAsync(string rabbitConnectionString, string mainQueueName, CancellationToken cancellationToken)
    {
        var dlq = PlaygroundRabbitQueues.DlqQueueName(mainQueueName);
        var factory = new ConnectionFactory { Uri = new Uri(rabbitConnectionString) };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        return await SafeMessageCountAsync(channel, dlq, cancellationToken);
    }

    private static async Task<uint> SafeMessageCountAsync(IChannel channel, string queueName, CancellationToken cancellationToken)
    {
        try
        {
            return await channel.MessageCountAsync(queueName, cancellationToken);
        }
        catch (OperationInterruptedException)
        {
            return 0;
        }
    }
}
