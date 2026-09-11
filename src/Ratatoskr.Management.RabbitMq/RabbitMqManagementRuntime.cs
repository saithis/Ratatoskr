using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Owns a UI replica's private reply and discovery queues. Discovery is fanout, never a competing consumer.</summary>
public sealed class RabbitMqManagementRuntime(
    RabbitMqConnectionManager connections,
    IOptions<RabbitMqManagementOptions> options,
    TimeProvider timeProvider) : BackgroundService, IManagementClient, IServiceCatalog, IManagementEventSource, IManagementEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ManagementResponseEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ServiceHeartbeat> _services = new(StringComparer.OrdinalIgnoreCase);
#pragma warning disable IDISP002 // Disposed when the hosted service is disposed.
    private readonly SemaphoreSlim _publishLock = new(1, 1);
#pragma warning restore IDISP002
    private IChannel? _channel;
    private string? _replyQueue;
    public event EventHandler<ServiceHeartbeatEventArgs>? ServiceUpdated;
    public event EventHandler<ServiceHeartbeatEventArgs>? AnnouncementReceived;

    public async Task<TResponse?> ExecuteAsync<TRequest, TResponse>(ManagementTarget target, string operation, TRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var timeout = options.Value.RequestTimeout;
        var envelope = new ManagementRequestEnvelope { ProtocolVersion = ManagementProtocol.Current, RequestId = Guid.NewGuid().ToString("N"), OperationId = Guid.NewGuid().ToString("N"), Target = target, Operation = operation, Deadline = timeProvider.GetUtcNow().Add(timeout), Payload = new ManagementPayload(typeof(TRequest).FullName ?? typeof(TRequest).Name, JsonSerializer.Serialize(request, JsonOptions)) };
        var completion = new TaskCompletionSource<ManagementResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(envelope.RequestId, completion)) throw new InvalidOperationException("Could not allocate management request correlation.");
        try
        {
            var channel = _channel ?? throw new InvalidOperationException("RabbitMQ management runtime has not started.");
            var properties = new BasicProperties { MessageId = envelope.OperationId, CorrelationId = envelope.RequestId, ReplyTo = _replyQueue, Expiration = ((long)timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture), ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };
            await _publishLock.WaitAsync(cancellationToken);
            try { await channel.BasicPublishAsync(exchange: Names.Commands(options.Value), routingKey: target.InstanceId is null ? target.LogicalServiceName : $"{target.LogicalServiceName}.{target.InstanceId}", mandatory: false, basicProperties: properties, body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions)), cancellationToken: cancellationToken); }
            finally { _publishLock.Release(); }
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var response = await completion.Task.WaitAsync(linked.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error?.SafeDetail ?? "Management operation failed.");
            return string.IsNullOrEmpty(response.PayloadJson) ? default : JsonSerializer.Deserialize<TResponse>(response.PayloadJson, JsonOptions);
        }
        finally { _pending.TryRemove(envelope.RequestId, out _); }
    }

    public IReadOnlyList<ServiceCardDto> GetAllServices() => _services.Values.Select(ToCard).OrderBy(x => x.ServiceName, StringComparer.OrdinalIgnoreCase).ToArray();
    public ServiceDetailDto? GetService(string serviceName) => _services.TryGetValue(serviceName, out var heartbeat) ? ToDetail(heartbeat) : null;
    public async ValueTask PublishAsync(ServiceHeartbeat announcement, CancellationToken cancellationToken = default)
    {
        var channel = _channel ?? throw new InvalidOperationException("RabbitMQ management runtime has not started.");
        await _publishLock.WaitAsync(cancellationToken);
        try { await channel.BasicPublishAsync(exchange: Names.Discovery(options.Value), routingKey: "", mandatory: false, basicProperties: new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Transient }, body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, JsonOptions)), cancellationToken: cancellationToken); }
        finally { _publishLock.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hosted-service lifecycle creates this exactly once.
#pragma warning disable IDISP003
        _channel = await connections.CreateChannelAsync(enablePublisherConfirms: true, cancellationToken: stoppingToken);
#pragma warning restore IDISP003
        var prefix = options.Value;
        await _channel.ExchangeDeclareAsync(exchange: Names.Commands(prefix), type: ExchangeType.Direct, durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
        await _channel.ExchangeDeclareAsync(exchange: Names.Discovery(prefix), type: ExchangeType.Fanout, durable: false, autoDelete: true, arguments: null, cancellationToken: stoppingToken);
        var reply = await _channel.QueueDeclareAsync(queue: $"{prefix.ExchangePrefix}.replies.{prefix.UiInstanceId}", durable: false, exclusive: true, autoDelete: true, arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-expires"] = (long)prefix.RequestTimeout.TotalMilliseconds * 3, ["x-message-ttl"] = (long)prefix.RequestTimeout.TotalMilliseconds, ["x-max-length"] = prefix.ResponseQueueMaxLength }, cancellationToken: stoppingToken);
        _replyQueue = reply.QueueName;
        var discovery = await _channel.QueueDeclareAsync(queue: $"{prefix.ExchangePrefix}.discovery.{prefix.UiInstanceId}", durable: false, exclusive: true, autoDelete: true, arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-expires"] = (long)prefix.HeartbeatInterval.TotalMilliseconds * 3, ["x-message-ttl"] = (long)prefix.HeartbeatInterval.TotalMilliseconds * 2, ["x-max-length"] = prefix.ResponseQueueMaxLength }, cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(queue: discovery.QueueName, exchange: Names.Discovery(prefix), routingKey: "", arguments: null, cancellationToken: stoppingToken);
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefix.PrefetchCount, global: false, cancellationToken: stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await _channel.BasicConsumeAsync(queue: _replyQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        await _channel.BasicConsumeAsync(queue: discovery.QueueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        try
        {
            if (delivery.BasicProperties.CorrelationId is { } correlation && _pending.TryGetValue(correlation, out var pending))
            {
                var response = JsonSerializer.Deserialize<ManagementResponseEnvelope>(delivery.Body.Span, JsonOptions);
                if (response is not null) pending.TrySetResult(response); else pending.TrySetException(new InvalidOperationException("Malformed management response."));
            }
            else
            {
                var heartbeat = JsonSerializer.Deserialize<ServiceHeartbeat>(delivery.Body.Span, JsonOptions);
                if (heartbeat is not null) { _services[heartbeat.ServiceName] = heartbeat; var args = new ServiceHeartbeatEventArgs(heartbeat); AnnouncementReceived?.Invoke(this, args); ServiceUpdated?.Invoke(this, args); }
            }
            await _channel!.BasicAckAsync(deliveryTag: delivery.DeliveryTag, multiple: false, cancellationToken: CancellationToken.None);
        }
        catch { await _channel!.BasicNackAsync(deliveryTag: delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: CancellationToken.None); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var request in _pending.Values) request.TrySetCanceled(cancellationToken);
        await base.StopAsync(cancellationToken);
        if (_channel is not null) { await _channel.CloseAsync(cancellationToken); _channel.Dispose(); }
    }
    public override void Dispose()
    {
        _channel?.Dispose();
        _publishLock.Dispose();
        base.Dispose();
    }
    private static ServiceCardDto ToCard(ServiceHeartbeat x) => new(x.ServiceName, "online", 1, x.DbContexts.Sum(d => d.PendingOutboxCount), x.DbContexts.Sum(d => d.PoisonedOutboxCount), x.DbContexts.Sum(d => d.PendingInboxCount), x.DbContexts.Sum(d => d.PoisonedInboxCount), x.Timestamp, x.DbContexts.Select(d => d.DbContextName).ToArray());
    private static ServiceDetailDto ToDetail(ServiceHeartbeat x) => new(x.ServiceName, "online", [new ServiceInstanceRecordDto(x.InstanceId, x.MachineName, x.Environment, x.StartedAt, x.Timestamp, true)], x.DbContexts, x.Channels);
}

internal static class Names { public static string Commands(RabbitMqManagementOptions o) => $"{o.ExchangePrefix}.commands"; public static string Discovery(RabbitMqManagementOptions o) => $"{o.ExchangePrefix}.discovery"; }
