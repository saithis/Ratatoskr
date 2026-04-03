using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.Core;

internal sealed class MessageSerializerResolver(
    ChannelRegistry channelRegistry,
    IMessageSerializer defaultSerializer,
    IEnumerable<IMessageSerializer> serializerCandidates,
    IServiceProvider serviceProvider) : IMessageSerializerResolver
{
    private readonly Dictionary<Type, Type> _serializerTypesByMessageType = BuildSerializerMap(channelRegistry);
    private readonly List<IMessageSerializer> _serializerCandidates = serializerCandidates.ToList();
    private readonly Dictionary<Type, IMessageSerializer> _resolvedBySerializerType = new();
    private readonly object _sync = new();

    public IMessageSerializer GetSerializer(Type messageType)
    {
        if (!_serializerTypesByMessageType.TryGetValue(messageType, out var serializerType))
            return defaultSerializer;

        if (serializerType.IsInstanceOfType(defaultSerializer))
            return defaultSerializer;

        lock (_sync)
        {
            if (_resolvedBySerializerType.TryGetValue(serializerType, out var serializer))
                return serializer;

            serializer = ResolveConfiguredSerializer(serializerType);
            _resolvedBySerializerType[serializerType] = serializer;
            return serializer;
        }
    }

    private IMessageSerializer ResolveConfiguredSerializer(Type serializerType)
    {
        var fromRegisteredSerializer = _serializerCandidates.FirstOrDefault(serializerType.IsInstanceOfType);
        if (fromRegisteredSerializer != null)
            return fromRegisteredSerializer;

        var fromTypedResolution = serviceProvider.GetService(serializerType);
        if (fromTypedResolution is IMessageSerializer typedSerializer)
            return typedSerializer;

        throw new InvalidOperationException(
            $"Serializer '{serializerType.FullName}' is configured for one or more messages, but was not registered in DI. " +
            $"Register it with services.AddSingleton<{serializerType.Name}>() or services.AddSingleton<IMessageSerializer, {serializerType.Name}>().");
    }

    private static Dictionary<Type, Type> BuildSerializerMap(ChannelRegistry channelRegistry)
    {
        var map = new Dictionary<Type, Type>();
        foreach (var channel in channelRegistry.GetAllChannels())
        {
            foreach (var message in channel.Messages)
            {
                var serializerType = message.SerializerType;
                if (serializerType == null)
                    continue;

                if (!map.TryGetValue(message.MessageType, out var existingSerializer))
                {
                    map[message.MessageType] = serializerType;
                    continue;
                }

                if (existingSerializer != serializerType)
                {
                    throw new InvalidOperationException(
                        $"Message type '{message.MessageType.FullName}' is configured with multiple serializers " +
                        $"('{existingSerializer.FullName}' and '{serializerType.FullName}'). " +
                        "Use a single serializer per message type.");
                }
            }
        }

        return map;
    }
}
