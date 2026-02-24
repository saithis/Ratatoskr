using Ratatoskr.CloudEvents;

namespace Ratatoskr.Core;

/// <summary>
/// Default implementation that enriches MessageProperties with metadata from the ChannelRegistry.
/// </summary>
public class MessagePropertiesEnricher(ChannelRegistry registry, CloudEventsOptions options, TimeProvider timeProvider, IEnumerable<ITransportMessageMetadataEnricher> transportEnrichers) : IMessagePropertiesEnricher
{
    public MessageProperties Enrich<TMessage>(MessageProperties? properties) where TMessage : notnull
    {
        return Enrich(typeof(TMessage), properties);
    }
    
    public MessageProperties Enrich(Type messageType, MessageProperties? properties)
    {
        properties ??= new MessageProperties();
        
        properties.Id ??= Guid.NewGuid().ToString();
        properties.Time ??= timeProvider.GetUtcNow();
        properties.Source ??= options.DefaultSource;
        
        var activity = System.Diagnostics.Activity.Current;
        if (activity != null)
        {
            properties.TraceParent = activity.Id;
            if (!string.IsNullOrEmpty(activity.TraceStateString))
            {
                properties.TraceState = activity.TraceStateString;
            }
        }
        
        // Query registry for type info
        var publishInfo = registry.GetPublishInformation(messageType);
        if (publishInfo == null)
        {
            // If not in registry, try to deduce type from attribute
            if (string.IsNullOrEmpty(properties.Type))
            {
                var attr = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<RatatoskrMessageAttribute>(messageType);
                if (attr != null)
                {
                    properties.Type = attr.Type;
                }
            }
            return properties;
        }
        
        // Enrich Type if not already set
        if (string.IsNullOrEmpty(properties.Type))
        {
            properties.Type = publishInfo.Message.MessageTypeName;
        }
            
        foreach (var enricher in transportEnrichers)
        {
            if (publishInfo.Channel.Transports.Count == 0 ||
                publishInfo.Channel.Transports.Contains(enricher.TransportName))
            {
                enricher.Enrich(publishInfo, properties);
            }
        }

        return properties;
    }
}
