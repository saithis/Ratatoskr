using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

internal class RabbitMqConsumer(
    RabbitMqConnectionManager connectionManager,
    ChannelRegistry registry,
    RabbitMqTopologyManager topologyManager,
    MessageDispatcher dispatcher,
    IRabbitMqEnvelopeMapper envelopeMapper,
    RabbitMqRetryHandler retryHandler,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<RabbitMqConsumer> logger)
    : BackgroundService
{
    private readonly List<IChannel> _channels = new();

    /// <summary>
    /// Gets whether the consumer is healthy (all channels are open).
    /// </summary>
    public virtual bool IsHealthy => _channels.Count > 0 && _channels.All(c => c.IsOpen);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting RabbitMQ consumer");

        // 1. Provision Topology First
        logger.LogInformation("Provisioning topology...");
        await topologyManager.ProvisionTopologyAsync(stoppingToken);

        // 2. Start Consumers for each Consumer Channel
        var consumerChannels = registry.GetConsumeChannels();

        foreach (var reg in consumerChannels)
        {
            var options = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();

            if (string.IsNullOrEmpty(options.QueueName))
            {
                logger.LogWarning("Skipping consumer channel '{Channel}' because no queue name is configured.", reg.ChannelName);
                continue;
            }

            var channel = await connectionManager.CreateChannelAsync(false, stoppingToken);
            await channel.BasicQosAsync(0, options.PrefetchCount, false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                await HandleMessageAsync(channel, ea, options, options.QueueName!, reg.ChannelName, stoppingToken);
            };

            logger.LogInformation("Starting consuming from queue '{Queue}' for channel '{Channel}'", options.QueueName, reg.ChannelName);

            await channel.BasicConsumeAsync(
                queue: options.QueueName!,
                autoAck: options.AutoAck,
                consumer: consumer,
                cancellationToken: stoppingToken);

            _channels.Add(channel);
        }

        // Keep running until cancelled
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleMessageAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions options,
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
            // Capture transport-level wire format before envelope mapping
            var transportMessage = RabbitMqTransportMessageFactory.FromDeliverEventArgs(ea);

            // Use envelope mapper to extract body and properties
            var (body, props) = envelopeMapper.MapIncoming(ea);
            messageTime = props.Time;

            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnMessageActivity(new MessageActivity
                    {
                        Stage = MessageStage.Received,
                        Properties = props,
                        SerializedBody = body,
                        TransportMessage = transportMessage,
                        Timestamp = timeProvider.GetUtcNow(),
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Message activity observer failed at the {Stage} stage", MessageStage.Received);
                }
            }

            tags = CreateTags(ea, props, queueName);

            RatatoskrDiagnostics.ReceiveMessages.Add(1, tags);

            if (messageTime.HasValue)
            {
                // Avoid negative lag due to clock skew
                var lag = Math.Max((DateTimeOffset.UtcNow - messageTime.Value).TotalMilliseconds, 0);
                RatatoskrDiagnostics.ReceiveLag.Record(lag, tags);
            }

            using var activity = StartActivity(props, tags, body.Length);

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
            logger.LogError(ex, "Error processing message '{MessageId}'", messageId);

            if (tags.Count == 0)
            {
                tags = CreateFallbackTags(ea, queueName);
            }

            if (!options.AutoAck)
            {
                await retryHandler.HandleFailureAsync(
                    channel, ea, options, queueName,
                    DispatchResult.RecoverableError, cancellationToken);
            }
        }
        finally
        {
             if (tags.Count > 0)
             {
                 tags.Add("outcome", outcome);
                 RatatoskrDiagnostics.ProcessDuration.Record(Stopwatch.GetElapsedTime(processStartTimestamp).TotalMilliseconds, tags);
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
            // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#messaging-attributes
            // https://opentelemetry.io/docs/specs/semconv/messaging/rabbitmq/
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
        RabbitMqChannelOptions options,
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
        logger.LogInformation("Stopping RabbitMQ consumer");

        await base.StopAsync(cancellationToken);

        foreach (var channel in _channels)
        {
            await channel.CloseAsync(cancellationToken);
            channel.Dispose();
        }
    }
}
