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
    RabbitMqOptions options,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<RabbitMqConsumer> logger)
    : BackgroundService
{
    private readonly List<IChannel> _channels = new();
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets whether the consumer is healthy (all channels are open).
    /// </summary>
    public virtual bool IsHealthy => _channels.Count > 0 && _channels.All(c => c.IsOpen);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting RabbitMQ consumer");

        var reconnectAttempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var channelClosedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                await ProvisionAndConsumeAsync(channelClosedCts, stoppingToken);

                // Reset reconnect backoff on successful connection
                reconnectAttempt = 0;

                // Keep running until a channel closes or we're asked to stop
                await Task.Delay(Timeout.Infinite, channelClosedCts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reconnectAttempt++;
                var delay = CalculateReconnectDelay(reconnectAttempt);

                logger.LogError(ex, "RabbitMQ consumer disconnected. Reconnecting in {Delay} (attempt {Attempt})...",
                    delay, reconnectAttempt);

                await CleanupChannelsAsync();

                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        logger.LogInformation("RabbitMQ consumer stopped");
    }

    private async Task ProvisionAndConsumeAsync(CancellationTokenSource channelClosedCts, CancellationToken stoppingToken)
    {
        // Provision Topology
        logger.LogInformation("Provisioning topology...");
        await topologyManager.ProvisionTopologyAsync(stoppingToken);

        // Start Consumers for each Consumer Channel
        var consumerChannels = registry.GetConsumeChannels();

        foreach (var reg in consumerChannels)
        {
            var channelOptions = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();

            if (string.IsNullOrEmpty(channelOptions.QueueName))
            {
                logger.LogWarning("Skipping consumer channel '{Channel}' because no queue name is configured.", reg.ChannelName);
                continue;
            }

            var channel = await connectionManager.CreateChannelAsync(false, stoppingToken);
            await channel.BasicQosAsync(0, channelOptions.PrefetchCount, false, stoppingToken);

            // Register channel close handler to trigger reconnection
            channel.ChannelShutdownAsync += (_, args) =>
            {
                logger.LogWarning("RabbitMQ channel closed: {ReplyCode} - {ReplyText}", args.ReplyCode, args.ReplyText);
                channelClosedCts.Cancel();
                return Task.CompletedTask;
            };

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                await HandleMessageAsync(channel, ea, channelOptions, channelOptions.QueueName!, reg.ChannelName, stoppingToken);
            };

            logger.LogInformation("Starting consuming from queue '{Queue}' for channel '{Channel}'", channelOptions.QueueName, reg.ChannelName);

            await channel.BasicConsumeAsync(
                queue: channelOptions.QueueName!,
                autoAck: channelOptions.AutoAck,
                consumer: consumer,
                cancellationToken: stoppingToken);

            _channels.Add(channel);
        }
    }

    private async Task CleanupChannelsAsync()
    {
        foreach (var channel in _channels)
        {
            try
            {
                if (channel.IsOpen)
                    await channel.CloseAsync();
                channel.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error cleaning up RabbitMQ channel during reconnection");
            }
        }
        _channels.Clear();
    }

    private static TimeSpan CalculateReconnectDelay(int attempt)
    {
        // Exponential backoff with equal jitter, capped at MaxReconnectDelay
        var baseDelay = Math.Min(
            InitialReconnectDelay.TotalSeconds * Math.Pow(2, attempt - 1),
            MaxReconnectDelay.TotalSeconds);
        var delaySeconds = baseDelay * 0.5 + baseDelay * 0.5 * Random.Shared.NextDouble();
        return TimeSpan.FromSeconds(delaySeconds);
    }

    private async Task HandleMessageAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions channelOptions,
        string queueName,
        string channelName,
        CancellationToken cancellationToken)
    {
        var messageId = ea.BasicProperties.MessageId ?? Guid.NewGuid().ToString();
        var processStartTimestamp = Stopwatch.GetTimestamp();
        string? errorType = null;
        TagList tags = default;
        DateTimeOffset? messageTime = null;
        Activity? activity = null;

        try
        {
            // Capture transport-level wire format before envelope mapping
            var transportMessage = RabbitMqTransportMessageSnapshotFactory.FromDeliverEventArgs(ea);

            // Use envelope mapper to extract body and properties
            var (body, props) = envelopeMapper.MapIncoming(ea);
            messageTime = props.Time;

            var receivedTimestamp = timeProvider.GetUtcNow();

            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnMessageActivity(new MessageActivity
                    {
                        Stage = MessageStage.Received,
                        Properties = props,
                        SerializedBody = body,
                        TransportName = RabbitMqConstants.TransportName,
                        TransportMessage = transportMessage,
                        Timestamp = receivedTimestamp,
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Message activity observer failed at the {Stage} stage", MessageStage.Received);
                }
            }

            tags = CreateTags(ea, props, queueName);

            RatatoskrDiagnostics.ClientConsumedMessages.Add(1, tags);

            if (messageTime.HasValue)
            {
                // Avoid negative lag due to clock skew
                var lag = Math.Max((receivedTimestamp - messageTime.Value).TotalSeconds, 0);
                RatatoskrDiagnostics.ReceiveLag.Record(lag, tags);
            }

            activity = StartActivity(props, tags, body.Length, ea.DeliveryTag);

            var result = await dispatcher.DispatchAsync(body, props, cancellationToken, channelName, RabbitMqConstants.TransportName);

            errorType = result switch
            {
                DispatchResult.Success => null,
                DispatchResult.Queued => null,
                DispatchResult.NoHandlers => "NoHandlerError",
                _ => "ProcessingError"
            };

            if (errorType != null)
            {
                activity?.SetTag(MessagingSemanticConventions.ErrorType, errorType);
                activity?.SetStatus(ActivityStatusCode.Error, errorType);
            }

            if (!channelOptions.AutoAck)
            {
               await HandleDispatchResultAsync(channel, ea, channelOptions, queueName, result, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing message '{MessageId}'", messageId);

            errorType = ex.GetType().FullName;

            activity?.SetTag(MessagingSemanticConventions.ErrorType, errorType);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            if (tags.Count == 0)
            {
                tags = CreateFallbackTags(ea, queueName);
            }

            if (!channelOptions.AutoAck)
            {
                await retryHandler.HandleFailureAsync(
                    channel, ea, channelOptions, queueName,
                    DispatchResult.RecoverableError, cancellationToken);
            }
        }
        finally
        {
             activity?.Dispose();

             if (tags.Count > 0)
             {
                 if (errorType != null)
                 {
                     tags.Add(MessagingSemanticConventions.ErrorType, errorType);
                 }

                 RatatoskrDiagnostics.ProcessDuration.Record(Stopwatch.GetElapsedTime(processStartTimestamp).TotalSeconds, tags);

                 if (messageTime.HasValue)
                 {
                      var lag = Math.Max((timeProvider.GetUtcNow() - messageTime.Value).TotalSeconds, 0);
                      RatatoskrDiagnostics.ProcessLag.Record(lag, tags);
                 }
             }
        }
    }

    private TagList CreateTags(BasicDeliverEventArgs ea, MessageProperties props, string queueName)
    {
        var (originalExchange, originalRoutingKey) = RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
        var destinationName = props.GetExchange() ?? originalExchange ?? ea.Exchange;
        var routingKey = props.GetRoutingKey() ?? originalRoutingKey ?? ea.RoutingKey;

        return BuildTagList(destinationName, routingKey, queueName);
    }

    private TagList CreateFallbackTags(BasicDeliverEventArgs ea, string queueName)
    {
        var (originalExchange, originalRoutingKey) = RabbitMqHeaderHelper.GetOriginalDestinationFromHeaders(ea.BasicProperties.Headers);
        var destinationName = originalExchange ?? ea.Exchange;
        var routingKey = originalRoutingKey ?? ea.RoutingKey;

        return BuildTagList(destinationName, routingKey, queueName);
    }

    private TagList BuildTagList(string destinationName, string routingKey, string queueName)
    {
        return new TagList
        {
            { MessagingSemanticConventions.System, "rabbitmq" },
            { MessagingSemanticConventions.OperationName, "process" },
            { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeProcess },
            { MessagingSemanticConventions.DestinationSubscriptionName, queueName },
            { MessagingSemanticConventions.DestinationName, destinationName },
            { MessagingSemanticConventions.RabbitMqRoutingKey, routingKey },
            { MessagingSemanticConventions.ServerAddress, options.ConnectionString?.Host },
            { MessagingSemanticConventions.ServerPort, options.ConnectionString?.Port },
        };
    }

    private Activity? StartActivity(MessageProperties props, TagList tags, int bodySize, ulong deliveryTag)
    {
        ActivityContext.TryParse(props.TraceParent, props.TraceState, out var parentContext);

        var destinationName = tags.FirstOrDefault(t => t.Key == MessagingSemanticConventions.DestinationName).Value as string;
        var destination = string.IsNullOrEmpty(destinationName)
            ? tags.FirstOrDefault(t => t.Key == MessagingSemanticConventions.DestinationSubscriptionName).Value as string
            : destinationName;

        var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            $"process {destination}",
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
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, bodySize);
            activity.SetTag(MessagingSemanticConventions.RabbitMqDeliveryTag, (long)deliveryTag);
        }
        return activity;
    }

    private async Task HandleDispatchResultAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions channelOptions,
        string queueName,
        DispatchResult result,
        CancellationToken cancellationToken)
    {
        switch (result)
        {
            case DispatchResult.Success:
            case DispatchResult.Queued:
                await channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
                break;

            case DispatchResult.NoHandlers:
            case DispatchResult.PermanentError:
            case DispatchResult.RecoverableError:
                await retryHandler.HandleFailureAsync(
                    channel, ea, channelOptions, queueName, result, cancellationToken);
                break;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping RabbitMQ consumer");

        await base.StopAsync(cancellationToken);

        await CleanupChannelsAsync();
    }
}
