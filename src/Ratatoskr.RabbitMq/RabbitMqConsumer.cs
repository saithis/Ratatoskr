using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

internal sealed partial class RabbitMqConsumer(
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
    ILogger<RabbitMqConsumer> logger
) : BackgroundService
{
    private readonly Lock _channelsLock = new();
    private readonly List<ConsumerChannel> _consumers = new();
    private int _inFlightHandlers;

    /// <summary>
    /// Gets whether the consumer is healthy (all channels are open).
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            lock (_channelsLock)
            {
                return _consumers.Count > 0 && _consumers.TrueForAll(c => c.Channel.IsOpen);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStartingConsumer(logger);

        await ProvisionAndConsumeAsync(stoppingToken);

        // Connection recovery is handled transparently by the RabbitMQ client
        // (AutomaticRecoveryEnabled + TopologyRecoveryEnabled). Wait until shutdown.
        await Task.Delay(Timeout.Infinite, stoppingToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        LogConsumerStopped(logger);
    }

    private async Task ProvisionAndConsumeAsync(CancellationToken stoppingToken)
    {
        LogProvisioningTopology(logger);
        await topologyManager.ProvisionTopologyAsync(stoppingToken);

        foreach (var reg in registry.GetConsumeChannels())
        {
            var channelOptions = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();

            if (string.IsNullOrEmpty(channelOptions.QueueName))
            {
                LogSkippingConsumerChannel(logger, reg.ChannelName);
                continue;
            }

            if (channelOptions is { AutoAck: true, ConcurrencyLimit: > 1 })
            {
                LogAutoAckPrefetchWarning(logger, reg.ChannelName, channelOptions.ConcurrencyLimit);
            }

            var consumerChannel = await ConsumerChannel.OpenAsync(
                connectionManager,
                channelOptions,
                reg.ChannelName,
                channelOptions.QueueName,
                stoppingToken
            );
            var channel = consumerChannel.Channel;

            channel.ChannelShutdownAsync += (_, args) =>
            {
                LogChannelClosed(logger, args.ReplyCode, args.ReplyText);
                return Task.CompletedTask;
            };

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                Interlocked.Increment(ref _inFlightHandlers);
                try
                {
                    await consumerChannel.ConcurrencyGate.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Interlocked.Decrement(ref _inFlightHandlers);
                    return;
                }
                _ = DispatchAfterGateAsync();

                async Task DispatchAfterGateAsync()
                {
                    try
                    {
                        await HandleMessageCoreAsync(consumerChannel, ea, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Service stopping; unacked messages will be re-delivered by RabbitMQ.
                    }
                    catch (Exception ex)
                    {
                        LogUnhandledExceptionDispatching(logger, ex, reg.ChannelName);
                    }
                    finally
                    {
                        consumerChannel.ConcurrencyGate.Release();
                        Interlocked.Decrement(ref _inFlightHandlers);
                    }
                }
            };

            LogStartingConsuming(logger, channelOptions.QueueName, reg.ChannelName);

            consumerChannel.ConsumerTag = await channel.BasicConsumeAsync(
                queue: channelOptions.QueueName,
                autoAck: channelOptions.AutoAck,
                consumer: consumer,
                cancellationToken: stoppingToken
            );

            lock (_channelsLock)
            {
                _consumers.Add(consumerChannel);
            }
        }
    }

    private async Task CancelConsumersAsync(CancellationToken cancellationToken)
    {
        List<ConsumerChannel> snapshot;
        lock (_channelsLock)
        {
            snapshot = [.. _consumers];
        }

        var cancelTasks = snapshot.Select(async entry =>
        {
            try
            {
                if (entry.Channel.IsOpen)
                {
                    await entry.Channel.BasicCancelAsync(
                        entry.ConsumerTag,
                        noWait: false,
                        cancellationToken
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogCancellationError(logger, ex, entry.ConsumerTag);
            }
        });

        await Task.WhenAll(cancelTasks);
    }

    private async Task WaitForInFlightDrainAsync(CancellationToken cancellationToken)
    {
        var timeout = options.ShutdownDrainTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        var deadline = timeProvider.GetUtcNow() + timeout;
        while (Volatile.Read(ref _inFlightHandlers) > 0)
        {
            if (timeProvider.GetUtcNow() >= deadline)
            {
                LogShutdownDrainTimeout(logger, timeout, Volatile.Read(ref _inFlightHandlers));
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LogShutdownDrainInterrupted(logger, Volatile.Read(ref _inFlightHandlers));
                return;
            }
        }
    }

    private async Task CleanupChannelsAsync()
    {
        List<ConsumerChannel> channelsToCleanup;
        lock (_channelsLock)
        {
            channelsToCleanup = [.. _consumers];
            _consumers.Clear();
        }

        foreach (var consumer in channelsToCleanup)
        {
            try
            {
                if (consumer.Channel.IsOpen)
                {
                    await consumer.Channel.CloseAsync(CancellationToken.None);
                }

                consumer.Dispose();
            }
            catch (Exception ex)
            {
                LogCleanupError(logger, ex);
            }
        }
    }

    private async Task HandleMessageCoreAsync(
        ConsumerChannel consumer,
        BasicDeliverEventArgs ea,
        CancellationToken cancellationToken
    )
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
            if (await HandleIfOversizedAsync(consumer, ea, messageId, cancellationToken))
            {
                return;
            }

            // Capture transport-level wire format before envelope mapping
            var transportMessage = RabbitMqTransportMessageSnapshotFactory.FromDeliverEventArgs(ea);

            // Use envelope mapper to extract body and properties
            var (body, props) = envelopeMapper.MapIncoming(ea);
            messageTime = props.Time;

            var receivedTimestamp = timeProvider.GetUtcNow();

            await observers.NotifyAsync(
                new MessageActivity
                {
                    Stage = MessageStage.Received,
                    Properties = props,
                    SerializedBody = body,
                    TransportName = RabbitMqConstants.TransportName,
                    TransportMessage = transportMessage,
                    Timestamp = receivedTimestamp,
                },
                logger
            );

            tags = telemetry.CreateConsumeTags(ea, props, consumer.QueueName);

            RabbitMqTelemetry.RecordReceived(tags, messageTime, receivedTimestamp);

            activity = RabbitMqTelemetry.StartConsumeActivity(
                props,
                tags,
                body.Length,
                ea.DeliveryTag
            );

            var result = await router.RouteAsync(
                body,
                props,
                RabbitMqConstants.TransportName,
                consumer.ChannelName,
                cancellationToken
            );

            errorType = result switch
            {
                DispatchResult.Success => null,
                DispatchResult.NoHandlers => "NoHandlerError",
                _ => "ProcessingError",
            };

            if (errorType != null)
            {
                activity?.SetTag(MessagingSemanticConventions.ErrorType, errorType);
                activity?.SetStatus(ActivityStatusCode.Error, errorType);
            }

            if (!consumer.Options.AutoAck)
            {
                await SettleAsync(consumer, ea, result, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LogErrorProcessingMessage(logger, ex, messageId);
            errorType = ex.GetType().FullName;
            activity?.SetTag(MessagingSemanticConventions.ErrorType, errorType);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            if (tags.Count == 0)
            {
                tags = telemetry.CreateConsumeFallbackTags(ea, consumer.QueueName);
            }

            if (!consumer.Options.AutoAck)
            {
                await SettleAsync(
                    consumer,
                    ea,
                    DispatchResult.RecoverableError,
                    cancellationToken
                );
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

    private async Task<bool> HandleIfOversizedAsync(
        ConsumerChannel consumer,
        BasicDeliverEventArgs ea,
        string messageId,
        CancellationToken cancellationToken
    )
    {
        if (
            !options.MaxInboundMessageSize.HasValue
            || ea.Body.Length <= options.MaxInboundMessageSize.Value
        )
        {
            return false;
        }

        LogOversizedMessageRejected(
            logger,
            ea.Body.Length,
            options.MaxInboundMessageSize.Value,
            messageId
        );
        if (consumer.Options.AutoAck)
        {
            LogOversizedMessageAutoAckError(logger, consumer.ChannelName);
            return true;
        }

        await SettleAsync(consumer, ea, DispatchResult.PermanentError, cancellationToken);
        return true;
    }

    /// <summary>
    /// Acks the delivery on success, otherwise hands it to the retry handler (retry, requeue or
    /// dead-letter). Serialized per channel through the ack lock because RabbitMQ channels are
    /// not safe for concurrent acks and publishes.
    /// </summary>
    private async Task SettleAsync(
        ConsumerChannel consumer,
        BasicDeliverEventArgs ea,
        DispatchResult result,
        CancellationToken cancellationToken
    )
    {
        await consumer.AckLock.WaitAsync(cancellationToken);
        try
        {
            if (result == DispatchResult.Success)
            {
                await consumer.Channel.BasicAckAsync(
                    ea.DeliveryTag,
                    multiple: false,
                    cancellationToken
                );
            }
            else
            {
                await retryHandler.HandleFailureAsync(
                    consumer.Channel,
                    ea,
                    consumer.Options,
                    consumer.QueueName,
                    result,
                    cancellationToken
                );
            }
        }
        finally
        {
            consumer.AckLock.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        LogStoppingConsumer(logger);

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

    /// <summary>
    /// A RabbitMQ channel consuming one queue, together with its per-channel synchronization.
    /// </summary>
    private sealed class ConsumerChannel : IDisposable
    {
        private ConsumerChannel(
            IChannel channel,
            RabbitMqChannelOptions options,
            string channelName,
            string queueName
        )
        {
            Channel = channel;
            Options = options;
            ChannelName = channelName;
            QueueName = queueName;
            ConcurrencyGate = new SemaphoreSlim(options.ConcurrencyLimit, options.ConcurrencyLimit);
        }

        /// <summary>Opens a channel with the queue's prefetch settings. The instance owns the channel.</summary>
        public static async Task<ConsumerChannel> OpenAsync(
            RabbitMqConnectionManager connectionManager,
            RabbitMqChannelOptions options,
            string channelName,
            string queueName,
            CancellationToken cancellationToken
        )
        {
            // Publisher confirms are enabled even though this is primarily a consume channel:
            // the managed dead-letter path republishes failed messages on this channel and then
            // acks the original. Without confirms a publish that the broker never persisted would
            // still be followed by the ack, losing the message. With confirms, BasicPublishAsync
            // throws on an unconfirmed publish so the ack is skipped and the message is redelivered.
            var channel = await connectionManager.CreateChannelAsync(
                enablePublisherConfirms: true,
                cancellationToken
            );
            try
            {
                await channel.BasicQosAsync(
                    prefetchSize: 0,
                    prefetchCount: options.PrefetchCount,
                    global: false,
                    cancellationToken: cancellationToken
                );
            }
            catch
            {
                channel.Dispose();
                throw;
            }

            return new ConsumerChannel(channel, options, channelName, queueName);
        }

        public IChannel Channel { get; }
        public RabbitMqChannelOptions Options { get; }

        /// <summary>The Ratatoskr channel name.</summary>
        public string ChannelName { get; }
        public string QueueName { get; }

        /// <summary>Set once <c>BasicConsumeAsync</c> has registered the consumer.</summary>
        public string ConsumerTag { get; set; } = string.Empty;

        /// <summary>Limits how many deliveries of this channel are handled concurrently.</summary>
        public SemaphoreSlim ConcurrencyGate { get; }

        /// <summary>Serializes acks, nacks and dead-letter publishes on <see cref="Channel"/>.</summary>
        public SemaphoreSlim AckLock { get; } = new(1, 1);

        public void Dispose()
        {
            Channel.Dispose();
            ConcurrencyGate.Dispose();
            AckLock.Dispose();
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Starting RabbitMQ consumer"
    )]
    private static partial void LogStartingConsumer(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Channel '{Channel}': AutoAck=true disables broker-side prefetch limiting; with ConcurrencyLimit={Limit} messages may accumulate faster than handlers process them."
    )]
    private static partial void LogAutoAckPrefetchWarning(
        ILogger logger,
        string channel,
        int limit
    );

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "RabbitMQ consumer stopped"
    )]
    private static partial void LogConsumerStopped(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Provisioning topology...")]
    private static partial void LogProvisioningTopology(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Skipping consumer channel '{Channel}' because no queue name is configured."
    )]
    private static partial void LogSkippingConsumerChannel(ILogger logger, string channel);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "RabbitMQ channel closed: {ReplyCode} - {ReplyText}"
    )]
    private static partial void LogChannelClosed(
        ILogger logger,
        ushort replyCode,
        string replyText
    );

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Unhandled exception dispatching message on channel '{Channel}'"
    )]
    private static partial void LogUnhandledExceptionDispatching(
        ILogger logger,
        Exception ex,
        string channel
    );

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Information,
        Message = "Starting consuming from queue '{Queue}' for channel '{Channel}'"
    )]
    private static partial void LogStartingConsuming(ILogger logger, string queue, string channel);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Debug,
        Message = "Error cancelling RabbitMQ consumer {ConsumerTag}"
    )]
    private static partial void LogCancellationError(
        ILogger logger,
        Exception ex,
        string consumerTag
    );

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Shutdown drain timed out after {Timeout}; {InFlight} handler(s) still in flight"
    )]
    private static partial void LogShutdownDrainTimeout(
        ILogger logger,
        TimeSpan timeout,
        int inFlight
    );

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "Shutdown drain interrupted by host cancellation; {InFlight} handler(s) still in flight"
    )]
    private static partial void LogShutdownDrainInterrupted(ILogger logger, int inFlight);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Debug,
        Message = "Error cleaning up RabbitMQ channel during shutdown"
    )]
    private static partial void LogCleanupError(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Inbound message size of {Size} bytes exceeds the configured maximum of {Max} bytes for message '{MessageId}'. Rejecting to DLQ."
    )]
    private static partial void LogOversizedMessageRejected(
        ILogger logger,
        int size,
        int max,
        string messageId
    );

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Error,
        Message = "MaxInboundMessageSize is configured, but channel '{Channel}' uses auto-ack; oversized message cannot be nacked to DLQ."
    )]
    private static partial void LogOversizedMessageAutoAckError(ILogger logger, string channel);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Error,
        Message = "Error processing message '{MessageId}'"
    )]
    private static partial void LogErrorProcessingMessage(
        ILogger logger,
        Exception ex,
        string messageId
    );

    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Information,
        Message = "Stopping RabbitMQ consumer"
    )]
    private static partial void LogStoppingConsumer(ILogger logger);
}
