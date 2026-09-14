using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Core;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq.Management;

/// <summary>Advertises the DLQ management capability.</summary>
internal sealed class RabbitMqCapabilityContributor : IManagementCapabilityContributor
{
    public IEnumerable<CapabilityDescriptor> GetCapabilities()
    {
        yield return new CapabilityDescriptor(ManagementCapabilityNames.Dlq, ManagementProtocol.Current);
    }
}

/// <summary>Requeues messages from a RabbitMQ Dead Letter Queue back to the main queue.</summary>
internal sealed class RabbitMqDlqRequeueOperation(
    ChannelRegistry channelRegistry,
    RabbitMqConnectionManager connectionManager,
    TimeProvider timeProvider
) : IManagementOperation
{
    public string Name => ManagementOperationNames.DlqRequeue;
    public Type RequestType => typeof(DlqRequeueRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        var request = context.RequestAs<DlqRequeueRequest>();
        var channelReg = channelRegistry
            .GetConsumeChannels()
            .FirstOrDefault(c => string.Equals(c.ChannelName, request.ChannelName, StringComparison.Ordinal));

        if (channelReg is null)
        {
            return ManagementResult.NotFound($"Channel '{request.ChannelName}' not found.", "channel_not_found");
        }

        var channelOpts = channelReg.GetRabbitMqChannelOptions();
        if (channelOpts is null)
        {
            return ManagementResult.Invalid($"Channel '{request.ChannelName}' is not a RabbitMQ channel.", "not_rabbitmq_channel");
        }

        var mainQueueName = request.QueueName ?? channelOpts.QueueName ?? channelReg.ChannelName;
        var dlqName = $"{mainQueueName}{channelOpts.Retry.DeadLetterSuffix}";

        await using var channel = await connectionManager.CreateChannelAsync(
            enablePublisherConfirms: false,
            cancellationToken
        );
        var maxToRequeue = request.Limit > 0 ? request.Limit.Value : 500;
        var requeuedCount = 0;

        while (requeuedCount < maxToRequeue)
        {
            BasicGetResult? getResult;
            try
            {
                getResult = await channel.BasicGetAsync(dlqName, autoAck: false, cancellationToken);
            }
            catch (OperationInterruptedException ex)
            {
                if (requeuedCount == 0)
                {
                    return ManagementResult.NotFound($"Dead letter queue '{dlqName}' was not found: {ex.Message}", "dlq_not_found");
                }
                break;
            }

            if (getResult is null)
            {
                break;
            }

            var props = new BasicProperties
            {
                MessageId = getResult.BasicProperties.MessageId,
                ContentType = getResult.BasicProperties.ContentType,
                DeliveryMode = getResult.BasicProperties.DeliveryMode,
                Type = getResult.BasicProperties.Type,
                Headers = getResult.BasicProperties.Headers is not null
                    ? new Dictionary<string, object?>(getResult.BasicProperties.Headers, StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal),
            };

            props.Headers.Remove("x-death");
            props.Headers["x-requeued-from-dlq-at"] = timeProvider.GetUtcNow().ToString("O");

            var exchangeName = channelOpts.AmqpExchangeName ?? channelReg.ChannelName;
            var routingKey = props.Type ?? "";

            try
            {
                await channel.BasicPublishAsync(
                    exchange: exchangeName,
                    routingKey: routingKey,
                    mandatory: false,
                    basicProperties: props,
                    body: getResult.Body,
                    cancellationToken: cancellationToken
                );
                await channel.BasicAckAsync(getResult.DeliveryTag, multiple: false, cancellationToken);
                requeuedCount++;
            }
            catch (OperationInterruptedException)
            {
                break;
            }
        }

        long remaining = 0;
        try
        {
            remaining = await channel.MessageCountAsync(dlqName, cancellationToken);
        }
        catch (OperationInterruptedException)
        {
            // Queue might be deleted or empty
        }

        return ManagementResult.Ok(new DlqRequeueResponse(mainQueueName, requeuedCount, remaining));
    }
}

/// <summary>Purges messages from a RabbitMQ Dead Letter Queue.</summary>
internal sealed class RabbitMqDlqPurgeOperation(
    ChannelRegistry channelRegistry,
    RabbitMqConnectionManager connectionManager
) : IManagementOperation
{
    public string Name => ManagementOperationNames.DlqPurge;
    public Type RequestType => typeof(DlqPurgeRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        var request = context.RequestAs<DlqPurgeRequest>();
        var channelReg = channelRegistry
            .GetConsumeChannels()
            .FirstOrDefault(c => string.Equals(c.ChannelName, request.ChannelName, StringComparison.Ordinal));

        if (channelReg is null)
        {
            return ManagementResult.NotFound($"Channel '{request.ChannelName}' not found.", "channel_not_found");
        }

        var channelOpts = channelReg.GetRabbitMqChannelOptions();
        if (channelOpts is null)
        {
            return ManagementResult.Invalid($"Channel '{request.ChannelName}' is not a RabbitMQ channel.", "not_rabbitmq_channel");
        }

        var mainQueueName = request.QueueName ?? channelOpts.QueueName ?? channelReg.ChannelName;
        var dlqName = $"{mainQueueName}{channelOpts.Retry.DeadLetterSuffix}";

        await using var channel = await connectionManager.CreateChannelAsync(
            enablePublisherConfirms: false,
            cancellationToken
        );
        uint purgedCount = 0;
        try
        {
            purgedCount = await channel.QueuePurgeAsync(dlqName, cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            return ManagementResult.NotFound($"Dead letter queue '{dlqName}' was not found: {ex.Message}", "dlq_not_found");
        }

        return ManagementResult.Ok(new DlqPurgeResponse(dlqName, purgedCount));
    }
}

/// <summary>Collects queue and DLQ statistics across all RabbitMQ consume channels.</summary>
internal sealed class RabbitMqQueueStatsOperation(
    ChannelRegistry channelRegistry,
    RabbitMqConnectionManager connectionManager
) : IManagementOperation
{
    public string Name => ManagementOperationNames.QueueStats;
    public Type RequestType => typeof(QueueStatsRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        var list = new List<ChannelQueueStats>();
        await using var channel = await connectionManager.CreateChannelAsync(
            enablePublisherConfirms: false,
            cancellationToken
        );

        foreach (var reg in channelRegistry.GetConsumeChannels())
        {
            var channelOpts = reg.GetRabbitMqChannelOptions();
            if (channelOpts is null)
            {
                continue;
            }

            var queueName = channelOpts.QueueName ?? reg.ChannelName;
            var dlqName = channelOpts.Retry.UseManaged ? $"{queueName}{channelOpts.Retry.DeadLetterSuffix}" : null;

            long queueCount = 0;
            long dlqCount = 0;

            try
            {
                queueCount = await channel.MessageCountAsync(queueName, cancellationToken);
            }
            catch (OperationInterruptedException) { }

            if (dlqName is not null)
            {
                try
                {
                    dlqCount = await channel.MessageCountAsync(dlqName, cancellationToken);
                }
                catch (OperationInterruptedException) { }
            }

            list.Add(new ChannelQueueStats(reg.ChannelName, queueName, queueCount, dlqName, dlqCount));
        }

        return ManagementResult.Ok(new QueueStatsResponse(list));
    }
}
