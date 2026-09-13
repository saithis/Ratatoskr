using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>
/// The dashboard's side of one RabbitMQ control plane: publishes commands, correlates replies.
/// </summary>
/// <remarks>
/// Replies arrive on a queue named after this replica and bound with its own routing key, so two
/// dashboard replicas never steal each other's answers. The queue is exclusive, which also means
/// it does not survive a disconnect — so a dropped connection fails every pending request at once
/// with <c>transport_unavailable</c> rather than leaving them waiting for a queue that is gone.
/// </remarks>
internal sealed partial class RabbitMqManagementTransport(
    string name,
    RabbitMqManagementConnection connection,
    IOptionsMonitor<RabbitMqManagementOptions> optionsMonitor,
    TimeProvider timeProvider,
    ILogger<RabbitMqManagementTransport> logger
) : BackgroundService, IManagementTransport
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ManagementResponseEnvelope>> _pending =
        new(StringComparer.Ordinal);

    private readonly TaskCompletionSource _readyCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IChannel? _publishChannel;
    private IChannel? _replyChannel;
    private RabbitMqManagementNames? _names;
    private RabbitMqCallerAuthenticator? _authenticator;
    private volatile bool _ready;

    public string Name { get; } = name;

    public ManagementTransportCapabilities Capabilities =>
        ManagementTransportCapabilities.LogicalServiceTargeting
        | ManagementTransportCapabilities.InstanceTargeting
        | ManagementTransportCapabilities.Discovery;

    private RabbitMqManagementOptions Options => optionsMonitor.Get(Name);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = Options;
        _names = RabbitMqManagementNames.Create(options);
        _authenticator = new RabbitMqCallerAuthenticator(options);
        connection.ConnectionLost += OnConnectionLost;

        try
        {
            // Publishing and reply consumption are separate channels. Sharing one means a burst of
            // concurrent commands waits for its own replies to be dispatched before their confirms
            // can land, which shows up as requests timing out under load and nowhere else.
            _publishChannel = await connection.CreateChannelAsync(
                publisherConfirms: true,
                cancellationToken: stoppingToken
            );
            _publishChannel.BasicReturnAsync += OnReturnedAsync;

            _replyChannel = await connection.CreateChannelAsync(
                publisherConfirms: false,
                consumerDispatchConcurrency: options.PrefetchCount,
                cancellationToken: stoppingToken
            );

            await DeclareAsync(_replyChannel, options, stoppingToken);
            _ready = true;
            _readyCompletion.TrySetResult();

            await Task.Delay(Timeout.Infinite, stoppingToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Management transport '{Name}' failed during startup.", Name);
            _readyCompletion.TrySetException(ex);
            throw;
        }
        finally
        {
            _ready = false;
            _readyCompletion.TrySetCanceled(stoppingToken);
            connection.ConnectionLost -= OnConnectionLost;
            FailPending(ManagementErrorCodes.TransportUnavailable, "The management transport stopped.");
        }
    }

    private async Task DeclareAsync(
        IChannel channel,
        RabbitMqManagementOptions options,
        CancellationToken cancellationToken
    )
    {
        var names = _names!;
        var replyQueue = names.ReplyQueue(options.ReplicaId);

        await channel.ExchangeDeclareAsync(
            names.ReplyExchange,
            ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken
        );

        // Named, not server-named: amq.gen-* falls outside a {user}\..* configure permission.
        // Exclusive, so it disappears with this replica rather than accumulating on the broker.
        await channel.QueueDeclareAsync(
            replyQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-message-ttl"] = (long)options.RequestTimeout.TotalMilliseconds,
            },
            cancellationToken: cancellationToken
        );
        await channel.QueueBindAsync(
            replyQueue,
            names.ReplyExchange,
            RabbitMqManagementNames.ReplyKey(options.ReplicaId),
            cancellationToken: cancellationToken
        );

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: options.PrefetchCount,
            global: false,
            cancellationToken: cancellationToken
        );

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnReplyAsync;
        await channel.BasicConsumeAsync(
            replyQueue,
            autoAck: true,
            consumer: consumer,
            cancellationToken: cancellationToken
        );

        LogReplyTopologyReady(logger, Name, names.ReplyExchange, replyQueue);
    }

    public async Task<ManagementResponseEnvelope> SendAsync(
        ManagementAddress address,
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(request);

        await EnsureReadyAsync(cancellationToken);

        var channel = _publishChannel;
        if (!_ready || channel is not { IsOpen: true })
        {
            return Failure(
                request,
                ManagementResultStatus.Unsupported,
                ManagementErrorCodes.TransportUnavailable,
                $"Management transport '{Name}' is not connected.",
                isRetryable: true
            );
        }

        var exchange = address.Get(RabbitMqAddressKeys.Exchange);
        var routingKey = request.Target.InstanceId is null
            ? address.Get(RabbitMqAddressKeys.ServiceKey)
            : RabbitMqManagementNames.InstanceKey(request.Target.InstanceId);

        if (string.IsNullOrWhiteSpace(exchange) || string.IsNullOrWhiteSpace(routingKey))
        {
            return Failure(
                request,
                ManagementResultStatus.NotFound,
                ManagementErrorCodes.TargetUnreachable,
                $"The announcement for '{request.Target.ServiceName}' carries no RabbitMQ address."
            );
        }

        var options = Options;
        var names = _names!;
        var addressed = request with
        {
            ReplyTo = $"{names.ReplyExchange}|{RabbitMqManagementNames.ReplyKey(options.ReplicaId)}",
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(addressed, ManagementJson.Options);
        var remaining = addressed.Deadline - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return Failure(
                request,
                ManagementResultStatus.Invalid,
                ManagementErrorCodes.DeadlineExceeded,
                "The request deadline had already elapsed before it was sent."
            );
        }

        return await SendRequestCoreAsync(
            channel,
            exchange,
            routingKey,
            addressed,
            body,
            remaining,
            cancellationToken
        );
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }

        try
        {
            using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                startupCts.Token
            );
            await _readyCompletion.Task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Startup deadline elapsed or caller cancelled.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Transport '{Name}' startup failed while awaiting readiness.", Name);
        }
    }

    private BasicProperties CreateRequestProperties(
        RabbitMqManagementOptions options,
        ManagementRequestEnvelope addressed,
        byte[] body,
        TimeSpan remaining
    )
    {
        return new BasicProperties
        {
            CorrelationId = addressed.RequestId,
            MessageId = addressed.OperationId.ToString("N"),
            ReplyTo = addressed.ReplyTo,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            // The broker drops the command once the caller has stopped waiting, so a target
            // that comes back an hour later does not replay an hour of stale instructions.
            Expiration = ((long)remaining.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            // Broker-validated, so the agent can authenticate this dashboard without a secret.
            UserId = RabbitMqManagementAgentService.UserIdFor(options),
            Headers = BuildHeaders(addressed, body),
        };
    }

    private async Task<ManagementResponseEnvelope> SendRequestCoreAsync(
        IChannel channel,
        string exchange,
        string routingKey,
        ManagementRequestEnvelope addressed,
        byte[] body,
        TimeSpan remaining,
        CancellationToken cancellationToken
    )
    {
        var completion = new TaskCompletionSource<ManagementResponseEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        if (!_pending.TryAdd(addressed.RequestId, completion))
        {
            return Failure(
                addressed,
                ManagementResultStatus.Conflict,
                ManagementErrorCodes.InternalError,
                "A request is already in flight under this correlation id."
            );
        }

        try
        {
            var properties = CreateRequestProperties(Options, addressed, body, remaining);

            try
            {
                // mandatory: an instance-targeted command whose replica is gone comes straight
                // back as unroutable, which is a far better answer than silence until the deadline.
                // A durable service queue with no consumer is *not* returned, which is equally
                // deliberate: the command waits for a replica to come back.
                await channel.BasicPublishAsync(
                    exchange,
                    routingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: cancellationToken
                );
            }
            catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
            {
                return Failure(
                    addressed,
                    ManagementResultStatus.NotFound,
                    ManagementErrorCodes.TargetUnreachable,
                    $"Exchange '{exchange}' does not exist; '{addressed.Target.ServiceName}' is not listening on transport '{Name}'."
                );
            }

            using var deadline = new CancellationTokenSource(remaining);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token
            );

            return await completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                addressed,
                ManagementResultStatus.Invalid,
                ManagementErrorCodes.DeadlineExceeded,
                $"'{addressed.Target.ServiceName}' did not answer within the request deadline.",
                isRetryable: true
            );
        }
        finally
        {
            _pending.TryRemove(addressed.RequestId, out _);
        }
    }

    private Dictionary<string, object?>? BuildHeaders(
        ManagementRequestEnvelope request,
        ReadOnlySpan<byte> body
    )
    {
        var signature = _authenticator!.Sign(request.OperationId, request.Deadline, body);
        if (signature is null)
        {
            return null;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RabbitMqCallerAuthenticator.SignatureHeader] = Encoding.UTF8.GetBytes(signature),
            [RabbitMqCallerAuthenticator.DeadlineHeader] = Encoding.UTF8.GetBytes(
                request.Deadline.UtcTicks.ToString(CultureInfo.InvariantCulture)
            ),
        };
    }

    private Task OnReplyAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var correlation = delivery.BasicProperties.CorrelationId;
        if (correlation is null || !_pending.TryGetValue(correlation, out var pending))
        {
            // A reply to a request that already timed out, or to another replica's request that
            // somehow landed here. Nothing to do; the queue is auto-acked so it is simply dropped.
            return Task.CompletedTask;
        }

        try
        {
            var response = JsonSerializer.Deserialize<ManagementResponseEnvelope>(
                delivery.Body.Span,
                ManagementJson.Options
            );

            pending.TrySetResult(
                response
                    ?? ManagementResponseEnvelope.Failed(
                        correlation,
                        Guid.Empty,
                        ManagementResultStatus.Invalid,
                        new ManagementError(
                            ManagementErrorCodes.InternalError,
                            "The service returned an empty response."
                        )
                    )
            );
        }
        catch (JsonException ex)
        {
            LogMalformedReply(logger, Name, ex);
            pending.TrySetResult(
                ManagementResponseEnvelope.Failed(
                    correlation,
                    Guid.Empty,
                    ManagementResultStatus.Invalid,
                    new ManagementError(
                        ManagementErrorCodes.InternalError,
                        "The service returned a response that could not be parsed."
                    )
                )
            );
        }

        return Task.CompletedTask;
    }

    private Task OnReturnedAsync(object sender, BasicReturnEventArgs args)
    {
        // 312 NO_ROUTE: nothing is bound to that routing key. For an instance target that means
        // the replica's exclusive queue is gone, so the command can be failed immediately instead
        // of waiting out the deadline.
        if (
            args.BasicProperties.CorrelationId is { } correlation
            && _pending.TryGetValue(correlation, out var pending)
        )
        {
            pending.TrySetResult(
                ManagementResponseEnvelope.Failed(
                    correlation,
                    Guid.TryParse(args.BasicProperties.MessageId, out var id) ? id : Guid.Empty,
                    ManagementResultStatus.NotFound,
                    new ManagementError(
                        ManagementErrorCodes.TargetUnreachable,
                        $"The command was returned unrouted ({args.ReplyCode} {args.ReplyText}); the target is not listening."
                    )
                )
            );
        }

        return Task.CompletedTask;
    }

    private void OnConnectionLost(object? sender, EventArgs args)
    {
        _ready = false;
        FailPending(
            ManagementErrorCodes.TransportUnavailable,
            "The management broker connection dropped, so this request can no longer be answered: "
                + "the reply queue was exclusive to the lost connection."
        );
    }

    private void FailPending(string code, string detail)
    {
        foreach (var (correlation, pending) in _pending)
        {
            pending.TrySetResult(
                ManagementResponseEnvelope.Failed(
                    correlation,
                    Guid.Empty,
                    ManagementResultStatus.Unsupported,
                    new ManagementError(code, detail, IsRetryable: true)
                )
            );
        }
    }

    private static ManagementResponseEnvelope Failure(
        ManagementRequestEnvelope request,
        ManagementResultStatus status,
        string code,
        string detail,
        bool isRetryable = false
    ) => ManagementResponseEnvelope.Failed(request, status, new ManagementError(code, detail, isRetryable));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_publishChannel is not null)
        {
            _publishChannel.BasicReturnAsync -= OnReturnedAsync;
            await _publishChannel.DisposeAsync().AsTask()
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _publishChannel = null;
        }

        if (_replyChannel is not null)
        {
            await _replyChannel.DisposeAsync().AsTask()
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _replyChannel = null;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Management transport {TransportName} is receiving replies on {Exchange} via queue {Queue}."
    )]
    private static partial void LogReplyTopologyReady(
        ILogger logger,
        string transportName,
        string exchange,
        string queue
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Management transport {TransportName} received a reply it could not parse."
    )]
    private static partial void LogMalformedReply(
        ILogger logger,
        string transportName,
        Exception exception
    );
}
