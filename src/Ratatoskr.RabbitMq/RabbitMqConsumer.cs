using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

internal sealed class RabbitMqConsumer(
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
    private readonly List<(
        IChannel Channel,
        string ConsumerTag,
        SemaphoreSlim ConcurrencyGate,
        SemaphoreSlim AckLock
    )> _consumers = new();
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
                return _consumers.Count > 0 && _consumers.All(c => c.Channel.IsOpen);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting RabbitMQ consumer");

        // Validate channel configurations upfront so a misconfigured channel causes an
        // immediate startup failure instead of surfacing as a confusing runtime error.
        foreach (var reg in registry.GetConsumeChannels())
        {
            var channelOptions = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();
            if (string.IsNullOrEmpty(channelOptions.QueueName))
            {
                continue;
            }

            ValidateChannelConcurrency(reg.ChannelName, channelOptions);

            if (channelOptions is { AutoAck: true, ConcurrencyLimit: > 1 })
            {
                logger.LogWarning(
                    "Channel '{Channel}': AutoAck=true disables broker-side prefetch limiting; with "
                        + "ConcurrencyLimit={Limit} messages may accumulate faster than handlers process them.",
                    reg.ChannelName,
                    channelOptions.ConcurrencyLimit
                );
            }
        }

        await ProvisionAndConsumeAsync(stoppingToken);

        // Connection recovery is handled transparently by the RabbitMQ client
        // (AutomaticRecoveryEnabled + TopologyRecoveryEnabled). Wait until shutdown.
        await Task.Delay(Timeout.Infinite, stoppingToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        logger.LogInformation("RabbitMQ consumer stopped");
    }

    private async Task ProvisionAndConsumeAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Provisioning topology...");
        await topologyManager.ProvisionTopologyAsync(stoppingToken);

        foreach (var reg in registry.GetConsumeChannels())
        {
            var channelOptions = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();

            if (string.IsNullOrEmpty(channelOptions.QueueName))
            {
                logger.LogWarning(
                    "Skipping consumer channel '{Channel}' because no queue name is configured.",
                    reg.ChannelName
                );
                continue;
            }

            var channel = await connectionManager.CreateChannelAsync(false, stoppingToken);
            await channel.BasicQosAsync(0, channelOptions.PrefetchCount, false, stoppingToken);
            var concurrencyGate = new SemaphoreSlim(
                channelOptions.ConcurrencyLimit,
                channelOptions.ConcurrencyLimit
            );
            var ackLock = new SemaphoreSlim(1, 1);

            channel.ChannelShutdownAsync += (_, args) =>
            {
                logger.LogWarning(
                    "RabbitMQ channel closed: {ReplyCode} - {ReplyText}",
                    args.ReplyCode,
                    args.ReplyText
                );
                return Task.CompletedTask;
            };

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (notNeededParam, ea) =>
            {
                Interlocked.Increment(ref _inFlightHandlers);
                try
                {
                    await concurrencyGate.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Interlocked.Decrement(ref _inFlightHandlers);
                    return;
                }
                _ = DispatchAfterGateAsync();
                return;

                async Task DispatchAfterGateAsync()
                {
                    try
                    {
                        await HandleMessageCoreAsync(
                            channel,
                            ea,
                            channelOptions,
                            channelOptions.QueueName,
                            reg.ChannelName,
                            ackLock,
                            stoppingToken
                        );
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // Service stopping; unacked messages will be re-delivered by RabbitMQ.
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Unhandled exception dispatching message on channel '{Channel}'",
                            reg.ChannelName
                        );
                    }
                    finally
                    {
                        concurrencyGate.Release();
                        Interlocked.Decrement(ref _inFlightHandlers);
                    }
                }
            };

            logger.LogInformation(
                "Starting consuming from queue '{Queue}' for channel '{Channel}'",
                channelOptions.QueueName,
                reg.ChannelName
            );

            var consumerTag = await channel.BasicConsumeAsync(
                queue: channelOptions.QueueName!,
                autoAck: channelOptions.AutoAck,
                consumer: consumer,
                cancellationToken: stoppingToken
            );

            lock (_channelsLock)
            {
                _consumers.Add((channel, consumerTag, concurrencyGate, ackLock));
            }
        }
    }

    private async Task CancelConsumersAsync(CancellationToken cancellationToken)
    {
        List<(
            IChannel Channel,
            string ConsumerTag,
            SemaphoreSlim ConcurrencyGate,
            SemaphoreSlim AckLock
        )> snapshot;
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
                logger.LogDebug(
                    ex,
                    "Error cancelling RabbitMQ consumer {ConsumerTag}",
                    entry.ConsumerTag
                );
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
                logger.LogWarning(
                    "Shutdown drain timed out after {Timeout}; {InFlight} handler(s) still in flight",
                    timeout,
                    Volatile.Read(ref _inFlightHandlers)
                );
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
                    Volatile.Read(ref _inFlightHandlers)
                );
                return;
            }
        }
    }

    private async Task CleanupChannelsAsync()
    {
        List<(
            IChannel Channel,
            SemaphoreSlim ConcurrencyGate,
            SemaphoreSlim AckLock
        )> channelsToCleanup;
        lock (_channelsLock)
        {
            channelsToCleanup =
            [
                .. _consumers.Select(c => (c.Channel, c.ConcurrencyGate, c.AckLock)),
            ];
            _consumers.Clear();
        }

        foreach (var (channel, concurrencyGate, ackLock) in channelsToCleanup)
        {
            try
            {
                if (channel.IsOpen)
                {
                    await channel.CloseAsync();
                }

                channel.Dispose();
                concurrencyGate.Dispose();
                ackLock.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error cleaning up RabbitMQ channel during shutdown");
            }
        }
    }

    private static void ValidateChannelConcurrency(
        string channelName,
        RabbitMqChannelOptions channelOptions
    )
    {
        if (
            channelOptions.PrefetchCount != 0
            && channelOptions.ConcurrencyLimit > channelOptions.PrefetchCount
        )
        {
            throw new InvalidOperationException(
                $"Invalid RabbitMQ channel configuration for '{channelName}': "
                    + $"ConcurrencyLimit ({channelOptions.ConcurrencyLimit}) must be less than or equal to "
                    + $"PrefetchCount ({channelOptions.PrefetchCount})."
            );
        }
    }

    private async Task HandleMessageCoreAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        RabbitMqChannelOptions channelOptions,
        string queueName,
        string channelName,
        SemaphoreSlim ackLock,
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
            if (
                options.MaxInboundMessageSize.HasValue
                && ea.Body.Length > options.MaxInboundMessageSize.Value
            )
            {
                logger.LogWarning(
                    "Inbound message size of {Size} bytes exceeds the configured maximum of {Max} bytes for message '{MessageId}'. Rejecting to DLQ.",
                    ea.Body.Length,
                    options.MaxInboundMessageSize.Value,
                    messageId
                );
                if (channelOptions.AutoAck)
                {
                    logger.LogError(
                        "MaxInboundMessageSize is configured, but channel '{Channel}' uses auto-ack; oversized message cannot be nacked to DLQ.",
                        channelName
                    );
                    return;
                }
                await ackLock.WaitAsync(cancellationToken);
                try
                {
                    await retryHandler.HandleFailureAsync(
                        channel,
                        ea,
                        channelOptions,
                        queueName,
                        DispatchResult.PermanentError,
                        cancellationToken
                    );
                }
                finally
                {
                    ackLock.Release();
                }
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

            tags = telemetry.CreateConsumeTags(ea, props, queueName);

            telemetry.RecordReceived(tags, messageTime, receivedTimestamp);

            activity = telemetry.StartConsumeActivity(props, tags, body.Length, ea.DeliveryTag);

            var result = await router.RouteAsync(
                body,
                props,
                RabbitMqConstants.TransportName,
                cancellationToken,
                channelName
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

            if (!channelOptions.AutoAck)
            {
                await HandleDispatchResultAsync(
                    channel,
                    ea,
                    channelOptions,
                    queueName,
                    result,
                    ackLock,
                    cancellationToken
                );
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
                await ackLock.WaitAsync(cancellationToken);
                try
                {
                    await retryHandler.HandleFailureAsync(
                        channel,
                        ea,
                        channelOptions,
                        queueName,
                        DispatchResult.RecoverableError,
                        cancellationToken
                    );
                }
                finally
                {
                    ackLock.Release();
                }
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
        SemaphoreSlim ackLock,
        CancellationToken cancellationToken
    )
    {
        await ackLock.WaitAsync(cancellationToken);
        try
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
                        channel,
                        ea,
                        channelOptions,
                        queueName,
                        result,
                        cancellationToken
                    );
                    break;
            }
        }
        finally
        {
            ackLock.Release();
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
