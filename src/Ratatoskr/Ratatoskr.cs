using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr;

public class Ratatoskr(
    IMessageSerializer serializer,
    IMessageSender sender,
    IMessagePropertiesEnricher enricher,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers) : IRatatoskr
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
        await sender.SendAsync(serializedMessage, props, cancellationToken);

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
                    Timestamp = timeProvider.GetUtcNow(),
                });
            }
            catch
            {
                // Observer failures must not affect the pipeline
            }
        }
    }
}