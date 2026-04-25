using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

internal class RabbitMqMessageSender(
    RabbitMqConnectionManager connectionManager,
    RabbitMqOptions options,
    IRabbitMqEnvelopeMapper envelopeMapper,
    RabbitMqTelemetry telemetry,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<RabbitMqMessageSender> logger)
    : IMessageSender, IAsyncDisposable
{
    private readonly IMessageActivityObserver[] _observers = observers.ToArray();
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    public string TransportName => RabbitMqConstants.TransportName;

    public async Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        var channel = await connectionManager.GetOrCreateSendChannelAsync(options.UsePublisherConfirms, cancellationToken);

        var basicProps = new BasicProperties();
        var exchange = props.GetExchange() ?? "";
        var routingKey = props.GetRoutingKey() ?? props.Type ?? "";
        var destination = string.IsNullOrEmpty(exchange) ? routingKey : exchange;

        using var activity = telemetry.StartSendActivity(props, content.Length, destination, routingKey);

        // Use envelope mapper to map properties and potentially wrap content
        var bodyToSend = envelopeMapper.MapOutgoing(content, props, basicProps);

        // Capture transport-level wire format after envelope mapping
        var transportMessage = RabbitMqTransportMessageSnapshotFactory.FromBasicProperties(
            basicProps, bodyToSend, exchange, routingKey);

        var startTimestamp = Stopwatch.GetTimestamp();
        Exception? publishException = null;

        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            await channel.BasicPublishAsync(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: basicProps,
                body: bodyToSend,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            publishException = ex;
            RabbitMqTelemetry.SetActivityError(activity, ex);
            throw;
        }
        finally
        {
            _publishLock.Release();
            telemetry.RecordSent(startTimestamp, publishException, destination, routingKey);

            await _observers.NotifyAsync(new MessageActivity
            {
                Stage = MessageStage.Sent,
                Properties = props,
                SerializedBody = bodyToSend,
                TransportName = TransportName,
                TransportMessage = transportMessage,
                Exception = publishException,
                Timestamp = timeProvider.GetUtcNow(),
            }, logger);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _publishLock.Dispose();
        await connectionManager.DisposeAsync();
    }
}
