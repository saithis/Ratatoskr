using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>
/// Serves management commands for one service over one RabbitMQ control plane, and announces the
/// service to whichever dashboard owns the configured discovery exchange.
/// </summary>
/// <remarks>
/// Three channels, chosen by blast radius: one consumes commands, one publishes replies, one
/// publishes heartbeats. Publishing to an exchange the receiver has not declared yet closes the
/// channel — an expected state when an agent starts before its dashboard — and that must never
/// take command serving down with it.
/// </remarks>
internal sealed partial class RabbitMqManagementAgentService(
    string transportName,
    RabbitMqManagementConnection connection,
    IOptionsMonitor<RabbitMqManagementOptions> optionsMonitor,
    IManagementDispatcher dispatcher,
    ManagementAgent agent,
    TimeProvider timeProvider,
    ILogger<RabbitMqManagementAgentService> logger
) : BackgroundService
{
    private static readonly Meter Meter = new("Ratatoskr.Management.RabbitMq");

    private static readonly Counter<long> RejectedCallers = Meter.CreateCounter<long>(
        "ratatoskr.management.rejected_callers",
        unit: "{command}",
        description: "Commands refused before dispatch because the caller could not be authenticated."
    );

    private static readonly Counter<long> MalformedCommands = Meter.CreateCounter<long>(
        "ratatoskr.management.malformed_commands",
        unit: "{command}",
        description: "Commands dropped because they could not be parsed."
    );

    private RabbitMqManagementOptions Options => optionsMonitor.Get(transportName);

    private IChannel? _commandChannel;
    private IChannel? _replyChannel;
    private SemaphoreSlim? _concurrency;
    private RabbitMqCallerAuthenticator? _authenticator;
    private RabbitMqManagementNames? _names;

    /// <summary>The address this agent advertises, once its topology exists.</summary>
    internal ManagementAddress Address { get; private set; } = ManagementAddress.Empty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = Options;
        _names = RabbitMqManagementNames.Create(options);
        _authenticator = new RabbitMqCallerAuthenticator(options);
        _concurrency = new SemaphoreSlim(options.ConsumerConcurrency, options.ConsumerConcurrency);

        connection.ConnectionRestored += OnConnectionRestored;

        try
        {
            // ConsumerConcurrency is what it says: the client offloads deliveries to the pool up to
            // this many at a time, so several commands really do run in parallel.
            _commandChannel = await connection.CreateChannelAsync(
                publisherConfirms: false,
                consumerDispatchConcurrency: (ushort)options.ConsumerConcurrency,
                cancellationToken: stoppingToken
            );
            _replyChannel = await connection.CreateChannelAsync(
                publisherConfirms: true,
                cancellationToken: stoppingToken
            );

            await DeclareAsync(_commandChannel, stoppingToken);

            // Heartbeats own the channel they can afford to lose. Publishing into a discovery exchange
            // the dashboard has not declared yet is a 404 that closes the channel, and that is a
            // routine boot-ordering state, not a failure of this agent.
            var heartbeats = PublishHeartbeatsAsync(stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await heartbeats.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            connection.ConnectionRestored -= OnConnectionRestored;
        }
    }

    private async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var options = Options;
        var names = _names!;
        var serviceQueue = names.ServiceQueue(agent.ServiceName);
        var instanceQueue = names.InstanceQueue(agent.ServiceName, agent.InstanceId);

        await channel.ExchangeDeclareAsync(
            names.CommandExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken
        );

        // Durable and shared: every replica of the service competes on it, so exactly one handles
        // each logical-service command and a dead replica captures nothing.
        await channel.QueueDeclareAsync(
            serviceQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            serviceQueue,
            names.CommandExchange,
            RabbitMqManagementNames.ServiceKey(agent.ServiceName),
            cancellationToken: cancellationToken
        );

        // Exclusive and named: the broker removes it when this replica disconnects, which is what
        // turns an instance-targeted command to a dead replica into an immediate unroutable return
        // rather than a wait for the deadline. Never server-named — amq.gen-* is outside the
        // configure permission.
        await channel.QueueDeclareAsync(
            instanceQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            instanceQueue,
            names.CommandExchange,
            RabbitMqManagementNames.InstanceKey(agent.InstanceId),
            cancellationToken: cancellationToken
        );

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: options.PrefetchCount,
            global: false,
            cancellationToken: cancellationToken
        );

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnCommandAsync;
        await channel.BasicConsumeAsync(
            serviceQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken
        );
        await channel.BasicConsumeAsync(
            instanceQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken
        );

        Address = ManagementAddress.From(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RabbitMqAddressKeys.Exchange] = names.CommandExchange,
                [RabbitMqAddressKeys.ServiceKey] = RabbitMqManagementNames.ServiceKey(agent.ServiceName),
                [RabbitMqAddressKeys.InstanceKey] = RabbitMqManagementNames.InstanceKey(agent.InstanceId),
            }
        );

        LogTopologyReady(logger, transportName, names.CommandExchange, serviceQueue, instanceQueue);
    }

    private async Task OnCommandAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = _commandChannel!;
        await _concurrency!.WaitAsync(delivery.CancellationToken);
        try
        {
            var rejection = _authenticator!.Authenticate(delivery.BasicProperties, delivery.Body.Span);
            if (rejection is not CallerRejection.None)
            {
                RejectedCallers.Add(
                    1,
                    new KeyValuePair<string, object?>("transport", transportName),
                    new KeyValuePair<string, object?>("reason", rejection.ToString())
                );
                LogRejectedCaller(logger, transportName, rejection.ToString(), delivery.BasicProperties.UserId ?? "(none)");

                await TryReplyAsync(delivery, UnauthenticatedResponse(delivery, rejection));
                await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: delivery.CancellationToken
                );
                return;
            }

            ManagementRequestEnvelope? request;
            try
            {
                request = JsonSerializer.Deserialize<ManagementRequestEnvelope>(
                    delivery.Body.Span,
                    ManagementJson.Options
                );
            }
            catch (JsonException ex)
            {
                request = null;
                LogMalformedCommand(logger, transportName, ex);
            }

            if (request is null)
            {
                // Nack without requeue: a message that cannot be parsed will not parse on the
                // second attempt either, and requeueing it poisons the consumer loop forever.
                MalformedCommands.Add(1, new KeyValuePair<string, object?>("transport", transportName));
                await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: delivery.CancellationToken
                );
                return;
            }

            var response = await dispatcher.DispatchAsync(request, delivery.CancellationToken);
            await TryReplyAsync(delivery, response);

            // Acked only after the reply has been confirmed. A crash in between replays safely:
            // the mutation's operation id is already committed, so the redelivery returns the
            // recorded result rather than mutating again.
            await channel.BasicAckAsync(
                delivery.DeliveryTag,
                multiple: false,
                cancellationToken: delivery.CancellationToken
            );
        }
        catch (Exception ex)
        {
            LogCommandFailed(logger, transportName, ex);
            try
            {
                await channel.BasicNackAsync(
                    delivery.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: delivery.CancellationToken
                );
            }
            catch (Exception nackFailure)
            {
                // The channel is already gone. The broker will redeliver on reconnect, and the
                // operation id makes that redelivery safe.
                LogNackFailed(logger, transportName, nackFailure);
            }
        }
        finally
        {
            _concurrency!.Release();
        }
    }

    private static ManagementResponseEnvelope UnauthenticatedResponse(
        BasicDeliverEventArgs delivery,
        CallerRejection rejection
    )
    {
        // The envelope may be unparseable or forged, so only the AMQP correlation is trusted here.
        _ = Guid.TryParse(delivery.BasicProperties.MessageId, out var operationId);
        return ManagementResponseEnvelope.Failed(
            delivery.BasicProperties.CorrelationId ?? string.Empty,
            operationId,
            ManagementResultStatus.Forbidden,
            new ManagementError(
                ManagementErrorCodes.UnauthenticatedCaller,
                $"The command was refused before dispatch: {rejection}."
            )
        );
    }

    private async Task TryReplyAsync(
        BasicDeliverEventArgs delivery,
        ManagementResponseEnvelope response
    )
    {
        if (!TryParseReplyAddress(delivery, out var exchange, out var routingKey))
        {
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(response, ManagementJson.Options);
        var properties = new BasicProperties
        {
            CorrelationId = response.RequestId,
            MessageId = response.OperationId.ToString("N"),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Expiration = ((long)Options.RequestTimeout.TotalMilliseconds).ToString(
                CultureInfo.InvariantCulture
            ),
        };

        try
        {
            await _replyChannel!.BasicPublishAsync(
                exchange,
                routingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: delivery.CancellationToken
            );
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            // The dashboard replica went away and took its reply exchange with it. Its request
            // will time out, which is the correct outcome; the only thing to repair here is the
            // channel, which the 404 closed.
            LogReplyTargetMissing(logger, transportName, exchange);
            await ReopenReplyChannelAsync(delivery.CancellationToken);
        }
        catch (Exception ex)
        {
            LogReplyFailed(logger, transportName, exchange, ex);
            await ReopenReplyChannelAsync(delivery.CancellationToken);
        }
    }

    private static bool TryParseReplyAddress(
        BasicDeliverEventArgs delivery,
        out string exchange,
        out string routingKey
    )
    {
        exchange = string.Empty;
        routingKey = string.Empty;

        var replyTo = delivery.BasicProperties.ReplyTo;
        if (string.IsNullOrWhiteSpace(replyTo))
        {
            return false;
        }

        // AMQP's reply_to is a single string, but a reply needs an exchange and a routing key:
        // publishing to the default exchange is refused outright under the permission model.
        var separator = replyTo.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == replyTo.Length - 1)
        {
            return false;
        }

        exchange = replyTo[..separator];
        routingKey = replyTo[(separator + 1)..];
        return true;
    }

    private void OnConnectionRestored(object? sender, EventArgs args) => _ = OnConnectionRestoredAsync();

    private async Task OnConnectionRestoredAsync()
    {
        try
        {
            var options = Options;
            if (_commandChannel is not null)
            {
                await _commandChannel.DisposeAsync().AsTask()
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _commandChannel = null;
            }

            _commandChannel = await connection.CreateChannelAsync(
                publisherConfirms: false,
                consumerDispatchConcurrency: (ushort)options.ConsumerConcurrency,
                cancellationToken: CancellationToken.None
            );

            await DeclareAsync(_commandChannel, CancellationToken.None);
            await ReopenReplyChannelAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Management agent on '{Transport}' failed to restore command topology after reconnect.", transportName);
        }
    }

    private async Task ReopenReplyChannelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_replyChannel is not null)
            {
                await _replyChannel.DisposeAsync();
            }

            _replyChannel = await connection.CreateChannelAsync(
                publisherConfirms: true,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            LogReplyChannelReopenFailed(logger, transportName, ex);
        }
    }

    private async Task PublishHeartbeatsAsync(CancellationToken stoppingToken)
    {
        var options = Options;
        if (string.IsNullOrWhiteSpace(options.DiscoveryExchange))
        {
            // No dashboard configured. The agent still serves commands; it just is not discovered.
            return;
        }

        IChannel? channel = null;
        var backoff = TimeSpan.Zero;
        var warnedAboutMissingExchange = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (backoff > TimeSpan.Zero)
                {
                    await Task.Delay(backoff, timeProvider, stoppingToken);
                }

                try
                {
                    channel ??= await connection.CreateChannelAsync(
                        publisherConfirms: true,
                        cancellationToken: stoppingToken
                    );

                    var announcement = await agent.DescribeAsync(stoppingToken) with
                    {
                        Address = Address,
                    };

                    await channel.BasicPublishAsync(
                        options.DiscoveryExchange,
                        routingKey: string.Empty,
                        mandatory: false,
                        basicProperties: new BasicProperties
                        {
                            ContentType = "application/json",
                            DeliveryMode = DeliveryModes.Transient,
                            UserId = UserIdFor(options),
                            Expiration = ((long)(options.HeartbeatInterval * 3).TotalMilliseconds)
                                .ToString(CultureInfo.InvariantCulture),
                        },
                        body: JsonSerializer.SerializeToUtf8Bytes(announcement, ManagementJson.Options),
                        cancellationToken: stoppingToken
                    );

                    backoff = TimeSpan.Zero;
                    warnedAboutMissingExchange = false;
                    await Task.Delay(options.HeartbeatInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // An agent that starts before its dashboard publishes into an exchange nobody
                    // has declared. The broker answers 404 and closes the channel. That is a boot
                    // ordering state, not an error: warn once, reopen, and back off until the
                    // dashboard declares it.
                    if (!warnedAboutMissingExchange)
                    {
                        LogDiscoveryUnavailable(logger, transportName, options.DiscoveryExchange, ex);
                        warnedAboutMissingExchange = true;
                    }

                    if (channel is not null)
                    {
                        await channel.DisposeAsync().AsTask()
                            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        channel = null;
                    }

                    backoff = NextBackoff(backoff, options.MaxPublishBackoff);
                }
            }
        }
        finally
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().AsTask()
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal static TimeSpan NextBackoff(TimeSpan current, TimeSpan cap) =>
        current <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromTicks(Math.Min(current.Ticks * 2, cap.Ticks));

    /// <summary>
    /// The broker validates this against the authenticated connection, so setting it truthfully
    /// costs nothing and gives the receiver a sender identity it can trust.
    /// </summary>
    internal static string? UserIdFor(RabbitMqManagementOptions options)
    {
        var userInfo = options.ConnectionString?.UserInfo;
        if (string.IsNullOrWhiteSpace(userInfo))
        {
            return null;
        }

        var separator = userInfo.IndexOf(':', StringComparison.Ordinal);
        return Uri.UnescapeDataString(separator < 0 ? userInfo : userInfo[..separator]);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_commandChannel is not null)
        {
            await _commandChannel.DisposeAsync().AsTask()
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_replyChannel is not null)
        {
            await _replyChannel.DisposeAsync().AsTask()
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

    }

    public override void Dispose()
    {
        _concurrency?.Dispose();
        base.Dispose();
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Management transport {TransportName} is serving commands on exchange {Exchange} (queues {ServiceQueue}, {InstanceQueue})."
    )]
    private static partial void LogTopologyReady(
        ILogger logger,
        string transportName,
        string exchange,
        string serviceQueue,
        string instanceQueue
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Management transport {TransportName} refused a command before dispatch ({Reason}); the publisher identified as {UserId}."
    )]
    private static partial void LogRejectedCaller(
        ILogger logger,
        string transportName,
        string reason,
        string userId
    );

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Management transport {TransportName} dropped a command it could not parse."
    )]
    private static partial void LogMalformedCommand(
        ILogger logger,
        string transportName,
        Exception exception
    );

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Management transport {TransportName} failed while handling a command."
    )]
    private static partial void LogCommandFailed(
        ILogger logger,
        string transportName,
        Exception exception
    );

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Debug,
        Message = "Management transport {TransportName} could not nack a failed command; its channel is gone."
    )]
    private static partial void LogNackFailed(
        ILogger logger,
        string transportName,
        Exception exception
    );

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Management transport {TransportName} could not reply: exchange {Exchange} no longer exists, so the caller has gone away."
    )]
    private static partial void LogReplyTargetMissing(
        ILogger logger,
        string transportName,
        string exchange
    );

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Management transport {TransportName} could not publish a reply to {Exchange}."
    )]
    private static partial void LogReplyFailed(
        ILogger logger,
        string transportName,
        string exchange,
        Exception exception
    );

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Management transport {TransportName} could not reopen its reply channel."
    )]
    private static partial void LogReplyChannelReopenFailed(
        ILogger logger,
        string transportName,
        Exception exception
    );

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Warning,
        Message = "Management transport {TransportName} cannot publish heartbeats to {Exchange} yet; retrying with backoff. This is expected when the agent starts before its dashboard."
    )]
    private static partial void LogDiscoveryUnavailable(
        ILogger logger,
        string transportName,
        string exchange,
        Exception exception
    );
}
