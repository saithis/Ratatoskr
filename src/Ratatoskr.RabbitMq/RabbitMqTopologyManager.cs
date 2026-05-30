using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Provisions RabbitMQ exchanges, queues, and bindings based on the Ratatoskr channel registry at application startup.
/// </summary>
public partial class RabbitMqTopologyManager(
    ChannelRegistry registry,
    RabbitMqConnectionManager connectionManager,
    ILogger<RabbitMqTopologyManager> logger
)
{
    private readonly TaskCompletionSource _provisioningTcs = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <summary>
    /// Returns a task that completes when topology provisioning has finished, or faults if provisioning failed.
    /// </summary>
    public Task WaitForProvisioningAsync(CancellationToken cancellationToken = default)
    {
        return _provisioningTcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Declares all exchanges, queues, and bindings for registered RabbitMQ channels over a fresh AMQP channel.
    /// </summary>
    public async Task ProvisionTopologyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await connectionManager.CreateChannelAsync(
                enablePublisherConfirms: true,
                cancellationToken
            );

            var allChannels = registry.GetPublishChannels().Concat(registry.GetConsumeChannels());

            // Declaring channels first (EventPublish, CommandConsume own their exchange),
            // then validating channels (CommandPublish, EventConsume expect the exchange to exist).
            foreach (
                var reg in allChannels.OrderBy(r =>
                    r.Intent is ChannelType.CommandPublish or ChannelType.EventConsume
                )
            )
            {
                if (!reg.IsRabbitMqChannel())
                {
                    continue;
                }

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

    private async Task ProvisionChannelAsync(
        IChannel channel,
        ChannelRegistration reg,
        CancellationToken token
    )
    {
        var channelOpts = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();

        await DeclareOrValidateExchangeAsync(channel, reg, channelOpts, token);

        if (reg.Intent is ChannelType.CommandConsume or ChannelType.EventConsume)
        {
            await ProvisionQueueAndBindingsAsync(channel, reg, channelOpts, token);
        }
    }

    private static string ResolveAmqpExchangeName(
        ChannelRegistration reg,
        RabbitMqChannelOptions channelOpts
    ) => channelOpts.AmqpExchangeName ?? reg.ChannelName;

    private async Task DeclareOrValidateExchangeAsync(
        IChannel channel,
        ChannelRegistration reg,
        RabbitMqChannelOptions channelOpts,
        CancellationToken token
    )
    {
        var exchangeName = ResolveAmqpExchangeName(reg, channelOpts);
        if (reg.Intent is ChannelType.EventPublish or ChannelType.CommandConsume)
        {
            // We OWN the exchange -> Declare it
            LogDeclaringExchange(logger, exchangeName, channelOpts.ExchangeType);
            await channel.ExchangeDeclareAsync(
                exchange: exchangeName,
                type: channelOpts.ExchangeType.ToRabbitMqString(),
                durable: channelOpts.ExchangeDurable,
                autoDelete: channelOpts.ExchangeAutoDelete,
                arguments: null,
                cancellationToken: token
            );
        }
        else
        {
            // We EXPECT the exchange -> Validate it (Passive Declare)
            LogValidatingExchangeExists(logger, exchangeName);
            try
            {
                await channel.ExchangeDeclarePassiveAsync(exchangeName, token);
            }
            catch (Exception ex)
            {
                LogExchangeValidationFailed(logger, ex, exchangeName, reg.Intent);
                throw;
            }
        }
    }

    private async Task ProvisionQueueAndBindingsAsync(
        IChannel channel,
        ChannelRegistration reg,
        RabbitMqChannelOptions channelOpts,
        CancellationToken token
    )
    {
        var queueName =
            channelOpts.QueueName
            ?? throw new InvalidOperationException(
                $"Queue name must be specified for consumer channel '{reg.ChannelName}'"
            );

        Dictionary<string, object?> queueArgs = new(
            channelOpts.QueueArguments,
            StringComparer.Ordinal
        );
        if (channelOpts.QueueType == QueueType.Quorum)
        {
            queueArgs["x-queue-type"] = "quorum";
        }

        if (channelOpts.Retry.UseManaged)
        {
            await ProvisionRetryTopologyAsync(channel, queueName, queueArgs, channelOpts, token);
        }

        LogDeclaringQueue(logger, queueName, reg.ChannelName);

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: channelOpts.QueueDurable,
            exclusive: channelOpts.QueueExclusive,
            autoDelete: channelOpts.QueueAutoDelete,
            arguments: queueArgs,
            cancellationToken: token
        );

        var exchangeName = ResolveAmqpExchangeName(reg, channelOpts);

        // Bindings
        foreach (var msg in reg.Messages)
        {
            var msgOpts = msg.GetRabbitMqOptions();

            var routingKey = msgOpts?.RoutingKey ?? msg.MessageTypeName;

            LogBindingQueue(logger, queueName, exchangeName, routingKey);

            await channel.QueueBindAsync(
                queue: queueName,
                exchange: exchangeName,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: token
            );
        }
    }

    private async Task ProvisionRetryTopologyAsync(
        IChannel channel,
        string queueName,
        Dictionary<string, object?> mainQueueArgs,
        RabbitMqChannelOptions channelOpts,
        CancellationToken token
    )
    {
        var dlqName = $"{queueName}{channelOpts.Retry.DeadLetterSuffix}";
        var retryQueueName = $"{queueName}{channelOpts.Retry.RetrySuffix}";

        LogProvisioningRetryTopology(logger, queueName, dlqName, retryQueueName);

        // 1. Declare DLQ Exchange (Fanout)
        await channel.ExchangeDeclareAsync(
            exchange: dlqName,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: token
        );

        // 2. Declare DLQ Queue
        var dlqArgs = new Dictionary<string, object?>(mainQueueArgs, StringComparer.Ordinal);
        dlqArgs.Remove("x-dead-letter-exchange");
        dlqArgs.Remove("x-dead-letter-routing-key");
        await channel.QueueDeclareAsync(
            queue: dlqName,
            durable: true, // DLQ should usually be durable to prevent data loss
            exclusive: false,
            autoDelete: false,
            arguments: dlqArgs, // Use same type/args as main queue
            cancellationToken: token
        );

        // 3. Bind DLQ Queue to DLQ Exchange
        await channel.QueueBindAsync(
            queue: dlqName,
            exchange: dlqName,
            routingKey: "",
            cancellationToken: token
        );

        // 4. Declare Retry Queue (TTL -> Main Queue)
        var retryArgs = new Dictionary<string, object?>(mainQueueArgs, StringComparer.Ordinal)
        {
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = queueName,
            ["x-message-ttl"] = (long)channelOpts.Retry.Delay.TotalMilliseconds,
        };

        await channel.QueueDeclareAsync(
            queue: retryQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: retryArgs,
            cancellationToken: token
        );

        // 5. Configure Main Queue to Dead-Letter to Retry Queue
        mainQueueArgs["x-dead-letter-exchange"] = "";
        mainQueueArgs["x-dead-letter-routing-key"] = retryQueueName;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Declaring exchange '{Exchange}' Type: {Type}"
    )]
    private static partial void LogDeclaringExchange(
        ILogger logger,
        string exchange,
        RabbitMqExchangeType type
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Validating exchange '{Exchange}' exists"
    )]
    private static partial void LogValidatingExchangeExists(ILogger logger, string exchange);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Critical,
        Message = "Exchange '{Exchange}' validation failed. It must exist for intent {Intent}."
    )]
    private static partial void LogExchangeValidationFailed(
        ILogger logger,
        Exception ex,
        string exchange,
        ChannelType intent
    );

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Declaring queue '{Queue}' for channel '{Channel}'"
    )]
    private static partial void LogDeclaringQueue(ILogger logger, string queue, string channel);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Binding queue '{Queue}' to exchange '{Exchange}' with key '{Key}'"
    )]
    private static partial void LogBindingQueue(
        ILogger logger,
        string queue,
        string exchange,
        string key
    );

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Provisioning retry topology for queue '{Queue}' (DLQ: {Dlq}, Retry: {Retry})"
    )]
    private static partial void LogProvisioningRetryTopology(
        ILogger logger,
        string queue,
        string dlq,
        string retry
    );
}
