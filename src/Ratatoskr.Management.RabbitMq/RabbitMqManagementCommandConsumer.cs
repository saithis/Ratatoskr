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

/// <summary>Adapter from the RabbitMQ command queue to the transport-neutral host.</summary>
public sealed class RabbitMqManagementCommandHost(IManagementCommandDispatcher dispatcher, Ratatoskr.Management.Agent.EfCoreManagementOperations operations) : IManagementCommandHost
{
    public Task<ManagementResponseEnvelope> HandleAsync(ManagementRequestEnvelope request, CancellationToken cancellationToken = default) => dispatcher.DispatchAsync(request, cancellationToken);
    public Task<ServiceHeartbeat> GetAnnouncementAsync(CancellationToken cancellationToken = default) => operations.BuildHeartbeatAsync(cancellationToken);
}

internal sealed class RabbitMqManagementCommandConsumer(
    RabbitMqConnectionManager connections,
    IManagementCommandHost host,
    IManagementEventPublisher publisher,
    IOptions<RabbitMqManagementOptions> options,
    IOptions<Ratatoskr.Management.Agent.RatatoskrManagementOptions> agentOptions) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Task<ManagementResponseEnvelope>> _completed = new(StringComparer.Ordinal);
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hosted-service lifecycle creates this exactly once.
#pragma warning disable IDISP003
        _channel = await connections.CreateChannelAsync(enablePublisherConfirms: true, cancellationToken: stoppingToken);
#pragma warning restore IDISP003
        var provider = options.Value;
        var agent = agentOptions.Value;
        await _channel.ExchangeDeclareAsync(exchange: Names.Commands(provider), type: ExchangeType.Direct, durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
        await _channel.ExchangeDeclareAsync(exchange: Names.Discovery(provider), type: ExchangeType.Fanout, durable: false, autoDelete: true, arguments: null, cancellationToken: stoppingToken);
        var queueArgs = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x-message-ttl"] = (long)provider.RequestTimeout.TotalMilliseconds, ["x-max-length"] = provider.ResponseQueueMaxLength, ["x-dead-letter-exchange"] = $"{provider.ExchangePrefix}.errors" };
        await _channel.ExchangeDeclareAsync(exchange: $"{provider.ExchangePrefix}.errors", type: ExchangeType.Fanout, durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
        var logicalQueue = $"{provider.ExchangePrefix}.commands.{agent.ServiceName}";
        var instanceQueue = $"{logicalQueue}.{agent.InstanceId}";
        await _channel.QueueDeclareAsync(queue: logicalQueue, durable: true, exclusive: false, autoDelete: false, arguments: queueArgs, cancellationToken: stoppingToken);
        await _channel.QueueDeclareAsync(queue: instanceQueue, durable: true, exclusive: false, autoDelete: false, arguments: queueArgs, cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(queue: logicalQueue, exchange: Names.Commands(provider), routingKey: agent.ServiceName, arguments: null, cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(queue: instanceQueue, exchange: Names.Commands(provider), routingKey: $"{agent.ServiceName}.{agent.InstanceId}", arguments: null, cancellationToken: stoppingToken);
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: provider.PrefetchCount, global: false, cancellationToken: stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += HandleAsync;
        await _channel.BasicConsumeAsync(queue: logicalQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        await _channel.BasicConsumeAsync(queue: instanceQueue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        await publisher.PublishAsync(await host.GetAnnouncementAsync(stoppingToken), stoppingToken);
        using var timer = new PeriodicTimer(provider.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await publisher.PublishAsync(await host.GetAnnouncementAsync(stoppingToken), stoppingToken);
    }

    private async Task HandleAsync(object sender, BasicDeliverEventArgs delivery)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ManagementRequestEnvelope>(delivery.Body.Span, JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(delivery.BasicProperties.ReplyTo)) { await _channel!.BasicNackAsync(deliveryTag: delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: CancellationToken.None); return; }
            var response = await _completed.GetOrAdd(
                request.OperationId,
                static (_, state) => state.Host.HandleAsync(state.Request, CancellationToken.None),
                (Host: host, Request: request));
            await _channel!.BasicPublishAsync(exchange: "", routingKey: delivery.BasicProperties.ReplyTo, mandatory: false, basicProperties: new BasicProperties { CorrelationId = request.RequestId, MessageId = request.OperationId, ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent }, body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, JsonOptions)), cancellationToken: CancellationToken.None);
            await _channel.BasicAckAsync(deliveryTag: delivery.DeliveryTag, multiple: false, cancellationToken: CancellationToken.None);
        }
        catch { await _channel!.BasicNackAsync(deliveryTag: delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: CancellationToken.None); }
    }
    public override async Task StopAsync(CancellationToken cancellationToken) { await base.StopAsync(cancellationToken); if (_channel is not null) { await _channel.CloseAsync(cancellationToken); _channel.Dispose(); } }
}
