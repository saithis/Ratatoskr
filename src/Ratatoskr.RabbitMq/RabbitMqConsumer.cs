using System.Diagnostics;
using System.Linq;
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
    MessageRouter router,
    IRabbitMqEnvelopeMapper envelopeMapper,
    RabbitMqTelemetry telemetry,
    RabbitMqRetryHandler retryHandler,
    RabbitMqOptions options,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<RabbitMqConsumer> logger)
    : BackgroundService
{
    private readonly Lock _channelsLock = new();
    private readonly List<(IChannel Channel, string ConsumerTag, SemaphoreSlim ConcurrencyGate)> _consumers = new();
    private int _inFlightHandlers;
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets whether the consumer is healthy (all channels are open).
    /// </summary>
    public virtual bool IsHealthy
    {
        get
        {
            lock (_channelsLock)
            {
                return _consumers.Count > 0 && _consumers.All(c => c.Channel.IsOpen);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting RabbitMQ consumer");

        // Validate channel configurations before entering the reconnect loop so a
        // misconfigured channel causes an immediate startup failure instead of being
        // silently retried forever as a transient disconnect.
        foreach (var reg in registry.GetConsumeChannels())
        {
            var channelOptions = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();
            if (string.IsNullOrEmpty(channelOptions.QueueName))
                continue;

            ValidateChannelConcurrency(reg.ChannelName, channelOptions);

            if (channelOptions.AutoAck && channelOptions.ConcurrencyLimit > 1)
                logger.LogWarning(
                    "Channel '{Channel}': AutoAck=true disables broker-side prefetch limiting; with " +
                    "ConcurrencyLimit={Limit} messages may accumulate faster than handlers process them.",
                    reg.ChannelName, channelOptions.ConcurrencyLimit);
        }

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

                // Drain in-flight dispatches before disposing channels/gates.
                // channelClosedCts is already cancelled at this point, so dispatches
                // notice cancellation quickly and the drain completes fast.
                await WaitForInFlightDrainAsync(stoppingToken);
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
            var concurrencyGate = new SemaphoreSlim(channelOptions.ConcurrencyLimit, channelOptions.ConcurrencyLimit);

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
                var delivery = CloneDelivery(ea);
                _ = DispatchWithConcurrencyAsync(
                    channel,
                    delivery,
                    channelOptions,
                    channelOptions.QueueName!,
                    reg.ChannelName,
                    concurrencyGate,
                    channelClosedCts.Token);
                await Task.CompletedTask;
            };

            logger.LogInformation("Starting consuming from queue '{Queue}' for channel '{Channel}'", channelOptions.QueueName, reg.ChannelName);

            var consumerTag = await channel.BasicConsumeAsync(
                queue: channelOptions.QueueName!,
                autoAck: channelOptions.AutoAck,
                consumer: consumer,
                cancellationToken: stoppingToken);

            lock (_channelsLock)
            {
                _consumers.Add((channel, consumerTag, concurrencyGate));
            }
        }
    }

    private async Task CancelConsumersAsync(CancellationToken cancellationToken)
    {
        List<(IChannel Channel, string ConsumerTag, SemaphoreSlim ConcurrencyGate)> snapshot;
        lock (_channelsLock)
        {
            snapshot = [.._consumers];
        }

        var cancelTasks = snapshot.Select(async entry =>
        {
            try
            {
                if (entry.Channel.IsOpen)
                    await entry.Channel.BasicCancelAsync(entry.ConsumerTag, noWait: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error cancelling RabbitMQ consumer {ConsumerTag}", entry.ConsumerTag);
            }
        });

        await Task.WhenAll(cancelTasks);
    }

    private async Task WaitForInFlightDrainAsync(CancellationToken cancellationToken)
    {
        var timeout = options.ShutdownDrainTimeout;
        if (timeout <= TimeSpan.Zero)
            return;

        var deadline = timeProvider.GetUtcNow() + timeout;
        while (Volatile.Read(ref _inFlightHandlers) > 0)
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                logger.LogWarning(
                    "Shutdown drain timed out after {Timeout}; {InFlight} handler(s) still in flight",
                    timeout,
                    Volatile.Read(ref _inFlightHandlers));
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Shutdown drain interrupted by host cancellation; {InFlight} handler(s) still in flight",
                    Volatile.Read(ref _inFlightHandlers));
                return;
            }
        }
    }

    private async Task CleanupChannelsAsync()
    {
        List<(IChannel Channel, SemaphoreSlim ConcurrencyGate)> channelsToCleanup;
        lock (_channelsLock)
        {
            channelsToCleanup = [.._consumers.Select(c => (c.Channel, c.ConcurrencyGate))];
            _consumers.Clear();
        }

        foreach (var (channel, concurrencyGate) in channelsToCleanup)
        {
            try
            {
                if (channel.IsOpen)
                    await channel.CloseAsync();
                channel.Dispose();
                concurrencyGate.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error cleaning up RabbitMQ channel during reconnection");
            }
        }
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

    private static BasicDeliverEventArgs CloneDelivery(BasicDeliverEventArgs ea)
    {
        return new BasicDeliverEventArgs(
            ea.ConsumerTag,
            ea.DeliveryTag,
            ea.Redelivered,
            ea.Exchange,
            ea.RoutingKey,
            ea.BasicProperties,
            ea.Body.ToArray());
    }

    private async Task DispatchWithConcurrencyAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions channelOptions,
        string queueName,
        string channelName,
        SemaphoreSlim concurrencyGate,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inFlightHandlers);
        var gateAcquired = false;
        try
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            gateAcquired = true;
            await HandleMessageCoreAsync(channel, ea, channelOptions, queueName, channelName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Channel closing or service stopping; unstarted messages will be re-delivered by RabbitMQ.
        }
        finally
        {
            if (gateAcquired)
                concurrencyGate.Release();
            Interlocked.Decrement(ref _inFlightHandlers);
        }
    }

    private static void ValidateChannelConcurrency(string channelName, RabbitMqChannelOptions channelOptions)
    {
        if (channelOptions.PrefetchCount != 0 && channelOptions.ConcurrencyLimit > channelOptions.PrefetchCount)
        {
            throw new InvalidOperationException(
                $"Invalid RabbitMQ channel configuration for '{channelName}': " +
                $"ConcurrencyLimit ({channelOptions.ConcurrencyLimit}) must be less than or equal to " +
                $"PrefetchCount ({channelOptions.PrefetchCount}).");
        }
    }

    private async Task HandleMessageCoreAsync(
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
            // Enforce inbound message size limit
            if (options.MaxInboundMessageSize.HasValue && ea.Body.Length > options.MaxInboundMessageSize.Value)
            {
                logger.LogWarning(
                    "Inbound message size of {Size} bytes exceeds the configured maximum of {Max} bytes for message '{MessageId}'. Rejecting to DLQ.", 
                    ea.Body.Length, options.MaxInboundMessageSize.Value, messageId);
                if (channelOptions.AutoAck)
                {
                    logger.LogError(
                        "MaxInboundMessageSize is configured, but channel '{Channel}' uses auto-ack; oversized message cannot be nacked to DLQ.",
                        channelName);
                    return;
                }
                await retryHandler.HandleFailureAsync(channel, ea, channelOptions, queueName, DispatchResult.PermanentError, cancellationToken);
                return;
            }

            // Capture transport-level wire format before envelope mapping
            var transportMessage = RabbitMqTransportMessageSnapshotFactory.FromDeliverEventArgs(ea);

            // Use envelope mapper to extract body and properties
            var (body, props) = envelopeMapper.MapIncoming(ea);
            messageTime = props.Time;

            var receivedTimestamp = timeProvider.GetUtcNow();

            await observers.NotifyAsync(new MessageActivity
            {
                Stage = MessageStage.Received,
                Properties = props,
                SerializedBody = body,
                TransportName = RabbitMqConstants.TransportName,
                TransportMessage = transportMessage,
                Timestamp = receivedTimestamp,
            }, logger);

            tags = telemetry.CreateConsumeTags(ea, props, queueName);

            telemetry.RecordReceived(tags, messageTime, receivedTimestamp);

            activity = telemetry.StartConsumeActivity(props, tags, body.Length, ea.DeliveryTag);

            var result = await router.RouteAsync(body, props, RabbitMqConstants.TransportName, cancellationToken, channelName);

            errorType = result switch
            {
                DispatchResult.Success => null,
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
                tags = telemetry.CreateConsumeFallbackTags(ea, queueName);
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
                 telemetry.RecordProcessed(tags, processStartTimestamp, messageTime, errorType);
             }
        }
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

        try
        {
            try
            {
                await CancelConsumersAsync(cancellationToken);
                await WaitForInFlightDrainAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown timeout may cancel mid-drain; still stop background work and close channels.
            }

            await base.StopAsync(cancellationToken);
        }
        finally
        {
            await CleanupChannelsAsync();
        }
    }
}
