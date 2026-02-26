using System.Reflection;
using Ratatoskr.CloudEvents;

namespace Ratatoskr.Core;

/// <summary>
/// Default implementation that enriches MessageProperties with metadata from the ChannelRegistry.
/// </summary>
public class MessagePropertiesEnricher(ChannelRegistry registry, CloudEventsOptions options, TimeProvider timeProvider, IEnumerable<ITransportMessageMetadataEnricher> transportEnrichers) : IMessagePropertiesEnricher
{
    private readonly Dictionary<string, ITransportMessageMetadataEnricher> _enrichersByTransport = transportEnrichers.ToDictionary(e => e.TransportName);
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
        
        // Enrich Type if not already set
        if (string.IsNullOrEmpty(properties.Type))
        {
            properties.Type = GetMessageType(messageType, publishInfo);
        }

        if (publishInfo != null)
        {
            foreach (var transport in publishInfo.Channel.Transports)
            {
                properties.Transports.Add(transport);
                if (_enrichersByTransport.TryGetValue(transport, out var transportEnricher))
                {
                    transportEnricher.Enrich(publishInfo, properties);
                }
            }
        }

        return properties;
    }

    private string? GetMessageType(Type messageType, PublishInformation? publishInfo)
    {
        if (publishInfo != null)
        {
            return publishInfo.Message.MessageTypeName;
        }
        
        var attr = messageType.GetCustomAttribute<RatatoskrMessageAttribute>();
        return attr?.Type;
    }
}
