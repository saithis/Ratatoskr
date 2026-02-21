using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

public class RabbitMqTopologyManager(
    ChannelRegistry registry,
    RabbitMqConnectionManager connectionManager,
    ILogger<RabbitMqTopologyManager> logger)
{
    private readonly TaskCompletionSource _provisioningTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForProvisioningAsync(CancellationToken cancellationToken = default)
    {
        return _provisioningTcs.Task.WaitAsync(cancellationToken);
    }

    public async Task ProvisionTopologyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await connectionManager.CreateChannelAsync(true, cancellationToken);

            var allChannels = registry.GetPublishChannels().Concat(registry.GetConsumeChannels());

            // Declaring channels first (EventPublish, CommandConsume own their exchange),
            // then validating channels (CommandPublish, EventConsume expect the exchange to exist).
            foreach (var reg in allChannels.OrderBy(r => r.Intent is ChannelType.CommandPublish or ChannelType.EventConsume))
            {
                if (!reg.IsRabbitMqChannel()) continue;
                await ProvisionChannelAsync(channel, reg, cancellationToken);
            }

            _provisioningTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            _provisioningTcs.TrySetException(ex);
            throw;
        }
    }

    private async Task ProvisionChannelAsync(IChannel channel, ChannelRegistration reg, CancellationToken token)
    {
        var channelOpts = reg.GetRabbitMqChannelOptions()
                          ?? new RabbitMqChannelOptions();

        // 1. Exchange Logic
        if (reg.Intent == ChannelType.EventPublish || reg.Intent == ChannelType.CommandConsume)
        {
            // We OWN the exchange -> Declare it
            logger.LogInformation("Declaring exchange '{Exchange}' Type: {Type}", reg.ChannelName, channelOpts.ExchangeType);
            await channel.ExchangeDeclareAsync(
                exchange: reg.ChannelName,
                type: channelOpts.ExchangeType.ToRabbitMqString(),
                durable: channelOpts.ExchangeDurable,
                autoDelete: channelOpts.ExchangeAutoDelete,
                arguments: null,
                cancellationToken: token);
        }
        else
        {
            // We EXPECT the exchange -> Validate it (Passive Declare)
            logger.LogInformation("Validating exchange '{Exchange}' exists", reg.ChannelName);
            try
            {
                await channel.ExchangeDeclarePassiveAsync(reg.ChannelName, token);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Exchange '{Exchange}' validation failed. It must exist for intent {Intent}.", reg.ChannelName, reg.Intent);
                throw;
            }
        }

        if (reg.Intent == ChannelType.CommandConsume || reg.Intent == ChannelType.EventConsume)
        {
            string queueName = channelOpts.QueueName ?? throw new InvalidOperationException($"Queue name must be specified for consumer channel '{reg.ChannelName}'");

            IDictionary<string, object?> queueArgs = new Dictionary<string, object?>(channelOpts.QueueArguments);
            if (channelOpts.QueueType == QueueType.Quorum)
            {
                queueArgs["x-queue-type"] = "quorum";
            }

            if (channelOpts.Retry.UseManaged)
            {
                var dlqName = $"{queueName}{channelOpts.Retry.DeadLetterSuffix}";
                var retryQueueName = $"{queueName}{channelOpts.Retry.RetrySuffix}";

                logger.LogInformation("Provisioning retry topology for queue '{Queue}' (DLQ: {Dlq}, Retry: {Retry})", queueName, dlqName, retryQueueName);

                // 1. Declare DLQ Exchange (Fanout)
                await channel.ExchangeDeclareAsync(
                    exchange: dlqName,
                    type: RabbitMQ.Client.ExchangeType.Fanout,
                    durable: true,
                    autoDelete: false,
                    arguments: null,
                    cancellationToken: token);

                // 2. Declare DLQ Queue
                var dlqArgs = new Dictionary<string, object?>(queueArgs);
                dlqArgs.Remove("x-dead-letter-exchange");
                dlqArgs.Remove("x-dead-letter-routing-key");
                await channel.QueueDeclareAsync(
                    queue: dlqName,
                    durable: true, // DLQ should usually be durable to prevent data loss
                    exclusive: false,
                    autoDelete: false,
                    arguments: dlqArgs, // Use same type/args as main queue
                    cancellationToken: token);

                // 3. Bind DLQ Queue to DLQ Exchange
                await channel.QueueBindAsync(
                    queue: dlqName,
                    exchange: dlqName,
                    routingKey: "",
                    cancellationToken: token);

                // 4. Declare Retry Queue (TTL -> Main Queue)
                var retryArgs = new Dictionary<string, object?>(queueArgs)
                {
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = queueName,
                    ["x-message-ttl"] = (long)channelOpts.Retry.Delay.TotalMilliseconds
                };

                await channel.QueueDeclareAsync(
                    queue: retryQueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: retryArgs,
                    cancellationToken: token);

                // 5. Configure Main Queue to Dead-Letter to Retry Queue
                queueArgs["x-dead-letter-exchange"] = "";
                queueArgs["x-dead-letter-routing-key"] = retryQueueName;
            }

            logger.LogInformation("Declaring queue '{Queue}' for channel '{Channel}'", queueName, reg.ChannelName);

            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: channelOpts.QueueDurable,
                exclusive: channelOpts.QueueExclusive,
                autoDelete: channelOpts.QueueAutoDelete,
                arguments: queueArgs,
                cancellationToken: token);

            // 4. Bindings
            foreach (var msg in reg.Messages)
            {
                var msgOpts = msg.GetRabbitMqOptions();

                string routingKey = msgOpts?.RoutingKey ?? msg.MessageTypeName;

                logger.LogInformation("Binding queue '{Queue}' to exchange '{Exchange}' with key '{Key}'", queueName, reg.ChannelName, routingKey);

                await channel.QueueBindAsync(
                    queue: queueName,
                    exchange: reg.ChannelName,
                    routingKey: routingKey,
                    arguments: null,
                    cancellationToken: token);
            }
        }
    }
}
