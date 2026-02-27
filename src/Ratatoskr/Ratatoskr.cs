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
        // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/
        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity("publish", ActivityKind.Producer);

        props = enricher.Enrich<TMessage>(props);

        if (activity != null)
        {
            activity.SetTag(MessagingSemanticConventions.OperationName, "publish");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeCreate);
            activity.SetTag(MessagingSemanticConventions.System, "ratatoskr");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
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
            activity?.SetTag(MessagingSemanticConventions.ErrorType, exceptions[0].GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, exceptions[0].Message);
            throw new AggregateException(
                $"One or more transports failed to send message '{props.Id}'", exceptions);
        }
    }
}
