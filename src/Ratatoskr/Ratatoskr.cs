using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr;

public class Ratatoskr(
    IMessageSerializer serializer,
    IEnumerable<IMessageSender> senders,
    IMessagePropertiesEnricher enricher,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<Ratatoskr> logger) : IRatatoskr
{
    public async Task PublishDirectAsync<TMessage>(
        TMessage message,
        MessageProperties? props = null,
        CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity("Ratatoskr.Publish", ActivityKind.Producer);

        props = enricher.Enrich<TMessage>(props);

        if (activity != null)
        {
            // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#messaging-attributes
            activity.SetTag("messaging.system", "ratatoskr");
            activity.SetTag("messaging.message.id", props.Id);
        }

        var serializedMessage = serializer.Serialize(message);
        props.ContentType = serializer.ContentType;

        if (props.Transports.Count == 0)
        {
            throw new InvalidOperationException($"No transport found for message '{typeof(TMessage)}'");
        }

        var sendersToUse = senders.Where(sender => props.Transports.Contains(sender.TransportName)).ToArray();
        if (!sendersToUse.Any())
        {
            throw new InvalidOperationException($"No transport found for message '{typeof(TMessage)}'");
        }
        
        List<Exception>? exceptions = null;
        foreach (var sender in sendersToUse)
        {
            Exception? sendException = null;
            try
            {
                await sender.SendAsync(serializedMessage, props, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Transport '{TransportName}' failed to send message '{MessageId}'",
                    sender.TransportName, props.Id);
                sendException = ex;
                exceptions ??= [];
                exceptions.Add(ex);
            }

            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnMessageActivity(new MessageActivity
                    {
                        Stage = MessageStage.Published,
                        Properties = props,
                        SerializedBody = serializedMessage,
                        Message = message,
                        MessageType = typeof(TMessage),
                        TransportName = sender.TransportName,
                        Exception = sendException,
                        Timestamp = timeProvider.GetUtcNow(),
                    });
                }
                catch
                {
                    // Observer failures must not affect the pipeline
                }
            }
        }

        if (exceptions is { Count: > 0 })
        {
            throw new AggregateException(
                $"One or more transports failed to send message '{props.Id}'", exceptions);
        }
    }
}
