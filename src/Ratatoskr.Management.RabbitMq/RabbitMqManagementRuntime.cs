using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Runtime;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Owns a UI replica's private reply and discovery queues. Discovery is fanout, never a competing consumer.</summary>
public sealed class RabbitMqManagementRuntime(
    RabbitMqConnectionManager connections,
    IOptions<RabbitMqManagementOptions> options,
    RabbitMqOptions rabbitMqOptions,
    TimeProvider timeProvider,
    IManagementCommandDispatcher? commandDispatcher = null) : BackgroundService, IManagementClient, IServiceCatalog, IManagementEventSource, IManagementEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<ManagementResponseEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly ServiceRegistry _registry = new(timeProvider, options.Value.HeartbeatInterval * 3);
#pragma warning disable IDISP002 // Disposed when the hosted service is disposed.
    private readonly SemaphoreSlim _publishLock = new(1, 1);
#pragma warning restore IDISP002
    private IChannel? _channel;
    private string? _replyQueue;
    private RabbitMqManagementResourceNames? _names;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _commandEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _receivesDiscovery = commandDispatcher is null;
    public event EventHandler<ServiceHeartbeatEventArgs>? ServiceUpdated
    {
        add => _registry.ServiceUpdated += value;
        remove => _registry.ServiceUpdated -= value;
    }
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
            if (!_commandEndpoints.TryGetValue(EndpointKey(target), out var commandInbox))
            {
                throw new InvalidOperationException($"No RabbitMQ management endpoint has been discovered for service '{target.LogicalServiceName}'.");
            }
            var properties = new BasicProperties { MessageId = envelope.OperationId, CorrelationId = envelope.RequestId, ReplyTo = _replyQueue, Expiration = ((long)timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture), ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent };
            await _publishLock.WaitAsync(cancellationToken);
            try { await channel.BasicPublishAsync(exchange: "", routingKey: commandInbox, mandatory: false, basicProperties: properties, body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions)), cancellationToken: cancellationToken); }
            finally { _publishLock.Release(); }
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            var response = await completion.Task.WaitAsync(linked.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error?.SafeDetail ?? "Management operation failed.");
            return string.IsNullOrEmpty(response.PayloadJson) ? default : JsonSerializer.Deserialize<TResponse>(response.PayloadJson, JsonOptions);
        }
        finally { _pending.TryRemove(envelope.RequestId, out _); }
    }

    public IReadOnlyList<ServiceCardDto> GetAllServices() => _registry.GetAllServices();
    public ServiceDetailDto? GetService(string serviceName) => _registry.GetService(serviceName);
    public async ValueTask PublishAsync(ServiceHeartbeat announcement, CancellationToken cancellationToken = default)
    {
        var channel = _channel ?? throw new InvalidOperationException("RabbitMQ management runtime has not started.");
        var names = _names ?? throw new InvalidOperationException("RabbitMQ management runtime has not started.");
        await _publishLock.WaitAsync(cancellationToken);
        try { await channel.BasicPublishAsync(exchange: "", routingKey: names.DiscoveryTargetInbox, mandatory: false, basicProperties: new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Transient }, body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, JsonOptions)), cancellationToken: cancellationToken); }
        finally { _publishLock.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hosted-service lifecycle creates this exactly once.
#pragma warning disable IDISP003
        _channel = await connections.CreateChannelAsync(enablePublisherConfirms: true, cancellationToken: stoppingToken);
#pragma warning restore IDISP003
        var prefix = options.Value;
        _names = RabbitMqManagementResourceNames.Create(prefix, rabbitMqOptions);
        var names = _names;
        var reply = await _channel.QueueDeclareAsync(queue: names.ReplyInbox, durable: false, exclusive: true, autoDelete: true, arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-expires"] = (long)prefix.RequestTimeout.TotalMilliseconds * 3, ["x-message-ttl"] = (long)prefix.RequestTimeout.TotalMilliseconds, ["x-max-length"] = prefix.ResponseQueueMaxLength }, cancellationToken: stoppingToken);
        _replyQueue = reply.QueueName;
        string? discoveryQueue = null;
        if (_receivesDiscovery)
        {
            discoveryQueue = (await _channel.QueueDeclareAsync(queue: names.LocalDiscoveryInbox, durable: false, exclusive: false, autoDelete: true, arguments: new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-expires"] = (long)prefix.HeartbeatInterval.TotalMilliseconds * 3, ["x-message-ttl"] = (long)prefix.HeartbeatInterval.TotalMilliseconds * 2, ["x-max-length"] = prefix.ResponseQueueMaxLength }, cancellationToken: stoppingToken)).QueueName;
        }
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefix.PrefetchCount, global: false, cancellationToken: stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await _channel.BasicConsumeAsync(queue: _replyQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        if (discoveryQueue is not null)
        {
            await _channel.BasicConsumeAsync(queue: discoveryQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        }
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
                if (heartbeat is not null && _registry.Publish(heartbeat))
                {
                    if (!string.IsNullOrWhiteSpace(heartbeat.ManagementEndpoint))
                    {
                        _commandEndpoints[EndpointKey(new ManagementTarget(heartbeat.ServiceName, InstanceId: heartbeat.InstanceId))] = heartbeat.ManagementEndpoint;
                        _commandEndpoints[EndpointKey(new ManagementTarget(heartbeat.ServiceName))] = heartbeat.ManagementEndpoint;
                    }
                    AnnouncementReceived?.Invoke(this, new ServiceHeartbeatEventArgs(heartbeat));
                }
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

    private static string EndpointKey(ManagementTarget target) => $"{target.LogicalServiceName}\u001f{target.InstanceId ?? string.Empty}";
}
