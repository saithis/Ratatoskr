using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq.WolverineMigration;

/// <summary>
/// RabbitMQ consumer that uses migrated queue names (with suffix) during migration.
/// </summary>
internal class WolverineMigrationConsumer(
    RabbitMqConnectionManager connectionManager,
    ChannelRegistry registry,
    WolverineMigrationTopologyManager topologyManager,
    MessageDispatcher dispatcher,
    IRabbitMqEnvelopeMapper envelopeMapper,
    RabbitMqRetryHandler retryHandler,
    WolverineMigrationOptions migrationOptions,
    ILogger<WolverineMigrationConsumer> logger)
    : BackgroundService
{
    private readonly List<IChannel> _channels = new();

    /// <summary>
    /// Gets whether the consumer is healthy (all channels are open).
    /// </summary>
    public virtual bool IsHealthy => _channels.Count > 0 && _channels.All(c => c.IsOpen);
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Migration] Starting RabbitMQ consumer with migrated queue names (suffix: {Suffix})", migrationOptions.QueueSuffix);
        
        // 1. Provision Topology First
        logger.LogInformation("[Migration] Provisioning topology...");
        await topologyManager.ProvisionTopologyAsync(stoppingToken);
        
        // 2. Start Consumers for each Consumer Channel with migrated queue names
        var consumerChannels = registry.GetConsumeChannels();

        foreach (var reg in consumerChannels)
        {
            var options = reg.GetRabbitMqConsumerOptions() ?? new RabbitMqConsumerOptions();

            // Queue name MUST be resolved (provisioning should have ensured it, or we assume it exists)
            if (string.IsNullOrEmpty(options.QueueName))
            {
                logger.LogWarning("[Migration] Skipping consumer channel '{Channel}' because no queue name is configured.", reg.ChannelName);
                continue;
            }

            // Apply migration suffix to queue name
            var migratedQueueName = topologyManager.GetMigratedQueueName(options.QueueName);
            
            logger.LogInformation("[Migration] Consuming from migrated queue '{MigratedQueue}' (original: '{OriginalQueue}')", 
                migratedQueueName, options.QueueName);

            var channel = await connectionManager.CreateChannelAsync(false, stoppingToken);
            await channel.BasicQosAsync(0, options.PrefetchCount, false, stoppingToken);
            
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                await HandleMessageAsync(channel, ea, options, migratedQueueName, reg.ChannelName, stoppingToken);
            };
            
            await channel.BasicConsumeAsync(
                queue: migratedQueueName,
                autoAck: options.AutoAck,
                consumer: consumer,
                cancellationToken: stoppingToken);
            
            _channels.Add(channel);
        }
        
        logger.LogInformation("[Migration] All consumer channels started successfully");
        
        // Keep running until cancelled
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
    
    private async Task HandleMessageAsync(
        IChannel channel, 
        BasicDeliverEventArgs ea,
        RabbitMqConsumerOptions options,
        string queueName,
        string channelName,
        CancellationToken cancellationToken)
    {
        var messageId = ea.BasicProperties.MessageId ?? Guid.NewGuid().ToString();
        var processStartTimestamp = Stopwatch.GetTimestamp();
        var outcome = "failure";
        TagList tags = default;
        DateTimeOffset? messageTime = null;

        try
        {
            // Use envelope mapper to extract body and properties
            var (body, props) = envelopeMapper.MapIncoming(ea);
            messageTime = props.Time;
            
            tags = CreateTags(ea, props, queueName);
            
            RatatoskrDiagnostics.ReceiveMessages.Add(1, tags);

            if (messageTime.HasValue)
            {
                // Avoid negative lag due to clock skew
                var lag = Math.Max((DateTimeOffset.UtcNow - messageTime.Value).TotalMilliseconds, 0);
                RatatoskrDiagnostics.ReceiveLag.Record(lag, tags);
            }
            
            using var activity = StartActivity(props, tags, body.Length);

            // Dispatcher handles finding the handler based on type info in props/body
            var result = await dispatcher.DispatchAsync(body, props, cancellationToken, channelName);
            
            outcome = result switch
            {
                DispatchResult.Success => "success",
                DispatchResult.NoHandlers => "no_handler",
                _ => "failure"
            };
            
            if (!options.AutoAck) 
            {
               await HandleDispatchResultAsync(channel, ea, options, queueName, result, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Migration] Error processing message '{MessageId}' from queue '{Queue}'", messageId, queueName);
            
            // If tags weren't initialized (e.g. envelope mapping failed), we try meaningful defaults
            if (tags.Count == 0)
            {
                tags = CreateFallbackTags(ea, queueName);
            }

            if (!options.AutoAck)
            {
                // Treat exception as recoverable error
                await retryHandler.HandleFailureAsync(
                    channel, ea, options, queueName, 
                    DispatchResult.RecoverableError, cancellationToken);
            }
        }
        finally
        {
             if (tags.Count > 0)
             {
                 RatatoskrDiagnostics.ProcessDuration.Record(Stopwatch.GetElapsedTime(processStartTimestamp).TotalMilliseconds, tags);
                 tags.Add("outcome", outcome);
                 RatatoskrDiagnostics.ProcessMessages.Add(1, tags);
                 
                 if (messageTime.HasValue)
                 {
                      var lag = Math.Max((DateTimeOffset.UtcNow - messageTime.Value).TotalMilliseconds, 0);
                      RatatoskrDiagnostics.ProcessLag.Record(lag, tags);
                 }
             }
        }
    }

    private static TagList CreateTags(BasicDeliverEventArgs ea, MessageProperties props, string queueName)
    {
        var (originalExchange, originalRoutingKey) = RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
        var destinationName = props.GetExchange() ?? originalExchange ?? ea.Exchange;
        var routingKey = props.GetRoutingKey() ?? originalRoutingKey ?? ea.RoutingKey;
        
        return new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.destination.subscription.name", queueName },
            { "messaging.destination.name", destinationName },
            { "messaging.rabbitmq.destination.routing_key", routingKey }
        };
    }

    private static TagList CreateFallbackTags(BasicDeliverEventArgs ea, string queueName)
    {
        var (originalExchange, originalRoutingKey) = RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
        var destinationName = originalExchange ?? ea.Exchange;
        var routingKey = originalRoutingKey ?? ea.RoutingKey;
            
         return new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.destination.subscription.name", queueName },
            { "messaging.destination.name", destinationName },
            { "messaging.rabbitmq.destination.routing_key", routingKey }
        };
    }

    private static Activity? StartActivity(MessageProperties props, TagList tags, int bodySize)
    {
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);

        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "Ratatoskr.Receive", 
            ActivityKind.Consumer, 
            parentContext);

        if (activity != null)
        {
            foreach (var tag in tags)
            {
                activity.SetTag(tag.Key, tag.Value);
            }
            activity.SetTag("messaging.message.id", props.Id);
            activity.SetTag("messaging.message.body.size", bodySize);
        }
        return activity;
    }

    private async Task HandleDispatchResultAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqConsumerOptions options,
        string queueName,
        DispatchResult result,
        CancellationToken cancellationToken)
    {
        switch (result)
        {
            case DispatchResult.Success:
                await channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
                break;
            
            case DispatchResult.NoHandlers:
            case DispatchResult.PermanentError:
            case DispatchResult.RecoverableError:
                await retryHandler.HandleFailureAsync(
                    channel, ea, options, queueName, result, cancellationToken);
                break;
        }
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[Migration] Stopping RabbitMQ consumer");
        
        foreach (var channel in _channels)
        {
            await channel.CloseAsync(cancellationToken);
            channel.Dispose();
        }
        
        await base.StopAsync(cancellationToken);
    }
}
