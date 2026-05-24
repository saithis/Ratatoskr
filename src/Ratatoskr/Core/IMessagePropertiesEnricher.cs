namespace Ratatoskr.Core;

/// <summary>
/// Service that enriches MessageProperties with metadata from the MessageTypeRegistry.
/// </summary>
public interface IMessagePropertiesEnricher
{
    /// <summary>
    /// Enriches the properties for a message of type TMessage.
    /// If properties is null, creates a new instance.
    /// Only populates missing values, never overwrites existing ones.
    /// </summary>
    public MessageProperties Enrich<TMessage>(MessageProperties? properties)
        where TMessage : notnull;

    /// <summary>
    /// Enriches the properties for a message of the given type.
    /// If properties is null, creates a new instance.
    /// Only populates missing values, never overwrites existing ones.
    /// </summary>
    public MessageProperties Enrich(Type messageType, MessageProperties? properties);
}
