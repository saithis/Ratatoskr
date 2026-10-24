namespace Ratatoskr.Core;

/// <summary>
/// The enriched properties and serialized body of a message that is about to be published
/// directly or staged in the outbox.
/// </summary>
internal readonly record struct PreparedMessage(MessageProperties Properties, byte[] Body);

/// <summary>
/// Shared first step of every publish path: enriches the message properties (id, time, type,
/// transports, ...) and serializes the message with the serializer registered for its type.
/// </summary>
internal sealed class OutgoingMessagePreparer(
    IMessagePropertiesEnricher enricher,
    IMessageSerializerResolver serializerResolver
)
{
    public PreparedMessage Prepare(object message, Type messageType, MessageProperties? properties)
    {
        var enrichedProperties = enricher.Enrich(messageType, properties);
        var serializer = serializerResolver.GetSerializer(messageType);
        var body = serializer.Serialize(message);
        enrichedProperties.ContentType = serializer.ContentType;
        return new PreparedMessage(enrichedProperties, body);
    }
}
