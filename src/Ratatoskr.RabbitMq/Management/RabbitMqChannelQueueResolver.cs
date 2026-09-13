using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Core;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq.Management;

/// <summary>
/// Resolves live queue depths and DLQ information for RabbitMQ channels.
/// </summary>
public sealed class RabbitMqChannelQueueResolver(
    ChannelRegistry channelRegistry,
    RabbitMqConnectionManager connectionManager
) : IChannelQueueResolver
{
    public async Task<IReadOnlyList<QueueTopology>> ResolveQueuesAsync(
        string channelName,
        ChannelIntent intent,
        IReadOnlyList<string> messageTypes,
        CancellationToken cancellationToken = default
    )
    {
        if (intent != ChannelIntent.Consume)
        {
            return [];
        }

        var channelReg = channelRegistry
            .GetConsumeChannels()
            .FirstOrDefault(c => string.Equals(c.ChannelName, channelName, StringComparison.Ordinal));

        if (channelReg is null)
        {
            return [];
        }

        var channelOpts = channelReg.GetRabbitMqChannelOptions();
        if (channelOpts is null)
        {
            return [];
        }

        var queueName = channelOpts.QueueName ?? channelReg.ChannelName;
        var hasDlq = channelOpts.Retry.UseManaged;
        var dlqName = hasDlq ? $"{queueName}{channelOpts.Retry.DeadLetterSuffix}" : null;

        long queueCount = 0;
        long dlqCount = 0;

        try
        {
            await using var channel = await connectionManager.CreateChannelAsync(false, cancellationToken);
            queueCount = await SafeMessageCountAsync(channel, queueName, cancellationToken);
            if (hasDlq && dlqName is not null)
            {
                dlqCount = await SafeMessageCountAsync(channel, dlqName, cancellationToken);
            }
        }
        catch (Exception)
        {
            // If the broker is unreachable or initializing, reporting 0 allows announcement to succeed
        }

        return [new QueueTopology(queueName, queueCount, dlqName, dlqCount)];
    }

    private static async Task<long> SafeMessageCountAsync(
        IChannel channel,
        string queueName,
        CancellationToken cancellationToken
    )
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
