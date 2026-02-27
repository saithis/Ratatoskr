using System.Diagnostics;
using RabbitMQ.Client;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq;

public class RabbitMqMessageSender(
    RabbitMqConnectionManager connectionManager,
    RabbitMqOptions options,
    IRabbitMqEnvelopeMapper envelopeMapper,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers)
    : IMessageSender, IAsyncDisposable
{
    public string TransportName => RabbitMqConstants.TransportName;

    public async Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        await using var channel = await connectionManager.CreateChannelAsync(options.UsePublisherConfirms, cancellationToken);

        var basicProps = new BasicProperties();

        var exchange = props.GetExchange() ?? "";
        var routingKey = props.GetRoutingKey() ?? props.Type ?? "";

        var destination = string.IsNullOrEmpty(exchange) ? routingKey : exchange;

        // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/
        // https://opentelemetry.io/docs/specs/semconv/messaging/rabbitmq/
        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            $"send {destination}",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            // Inject current trace context into headers for propagation
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag(MessagingSemanticConventions.OperationName, "send");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend);
            activity.SetTag(MessagingSemanticConventions.System, "rabbitmq");
            activity.SetTag(MessagingSemanticConventions.DestinationName, destination);
            activity.SetTag(MessagingSemanticConventions.RabbitMqRoutingKey, routingKey);
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, content.Length);
            activity.SetTag(MessagingSemanticConventions.ServerAddress, options.ConnectionString?.Host);
            activity.SetTag(MessagingSemanticConventions.ServerPort, options.ConnectionString?.Port);
        }

        // Use envelope mapper to map properties and potentially wrap content
        var bodyToSend = envelopeMapper.MapOutgoing(content, props, basicProps);

        // Capture transport-level wire format after envelope mapping
        var transportMessage = RabbitMqTransportMessageSnapshotFactory.FromBasicProperties(
            basicProps, bodyToSend, exchange, routingKey);

        // In RabbitMQ.Client 7.x with publisher confirms enabled,
        // BasicPublishAsync returns a ValueTask that completes when the message is confirmed
        var startTimestamp = Stopwatch.GetTimestamp();

        Exception? publishException = null;

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
            activity?.SetTag(MessagingSemanticConventions.ErrorType, ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

            var tags = new TagList
            {
                { MessagingSemanticConventions.System, "rabbitmq" },
                { MessagingSemanticConventions.OperationName, "send" },
                { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend },
                { MessagingSemanticConventions.DestinationName, destination },
                { MessagingSemanticConventions.RabbitMqRoutingKey, routingKey },
                { MessagingSemanticConventions.ServerAddress, options.ConnectionString?.Host },
                { MessagingSemanticConventions.ServerPort, options.ConnectionString?.Port },
            };

            if (publishException != null)
            {
                tags.Add(MessagingSemanticConventions.ErrorType, publishException.GetType().FullName);
            }

            RatatoskrDiagnostics.ClientOperationDuration.Record(duration, tags);
            RatatoskrDiagnostics.ClientSentMessages.Add(1, tags);

            var sentTimestamp = timeProvider.GetUtcNow();

            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnMessageActivity(new MessageActivity
                    {
                        Stage = MessageStage.Sent,
                        Properties = props,
                        SerializedBody = transportMessage.Body,
                        TransportName = TransportName,
                        TransportMessage = transportMessage,
                        Exception = publishException,
                        Timestamp = sentTimestamp,
                    });
                }
                catch
                {
                    // Observer failures must not affect the pipeline
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await connectionManager.DisposeAsync();
    }
}
