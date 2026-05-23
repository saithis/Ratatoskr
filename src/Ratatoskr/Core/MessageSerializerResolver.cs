using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.Core;

internal sealed class MessageSerializerResolver(
    ChannelRegistry channelRegistry,
    IMessageSerializer defaultSerializer,
    IEnumerable<IMessageSerializer> serializerCandidates,
    IServiceProvider serviceProvider
) : IMessageSerializerResolver
{
    private readonly FrozenDictionary<Type, IMessageSerializer> _serializerByMessageType =
        BuildSerializerMap(
            channelRegistry,
            defaultSerializer,
            serializerCandidates,
            serviceProvider
        );

    public IMessageSerializer GetSerializer(Type messageType)
    {
        return _serializerByMessageType.GetValueOrDefault(messageType) ?? defaultSerializer;
    }

    private static IMessageSerializer ResolveConfiguredSerializer(
        Type serializerType,
        IMessageSerializer defaultSerializer,
        IEnumerable<IMessageSerializer> serializerCandidates,
        IServiceProvider serviceProvider
    )
    {
        if (serializerType.IsInstanceOfType(defaultSerializer))
            return defaultSerializer;

        var fromRegisteredSerializer = serializerCandidates.FirstOrDefault(
            serializerType.IsInstanceOfType
        );
        if (fromRegisteredSerializer != null)
            return fromRegisteredSerializer;

        var fromTypedResolution = serviceProvider.GetService(serializerType);
        if (fromTypedResolution is IMessageSerializer typedSerializer)
            return typedSerializer;

        throw new InvalidOperationException(
            $"Serializer '{serializerType.FullName}' is configured for one or more messages, but was not registered in DI. "
                + $"Register it as its concrete type, for example services.AddSingleton<{serializerType.Name}>()."
        );
    }

    private static FrozenDictionary<Type, IMessageSerializer> BuildSerializerMap(
        ChannelRegistry channelRegistry,
        IMessageSerializer defaultSerializer,
        IEnumerable<IMessageSerializer> serializerCandidates,
        IServiceProvider serviceProvider
    )
    {
        var serializerTypeMap = new Dictionary<Type, Type?>();
        foreach (var channel in channelRegistry.GetAllChannels())
        {
            foreach (var message in channel.Messages)
            {
                var serializerType = message.SerializerType;
                if (
                    !serializerTypeMap.TryGetValue(
                        message.MessageType,
                        out var existingSerializerType
                    )
                )
                {
                    serializerTypeMap[message.MessageType] = serializerType;
                    continue;
                }

                if (existingSerializerType == serializerType)
                    continue;

                if (existingSerializerType == null || serializerType == null)
                {
                    throw new InvalidOperationException(
                        $"Message type '{message.MessageType.FullName}' mixes default and explicit serializer registrations. "
                            + "Use a single serializer configuration per message type across all channels."
                    );
                }

                throw new InvalidOperationException(
                    $"Message type '{message.MessageType.FullName}' is configured with multiple serializers "
                        + $"('{existingSerializerType.FullName}' and '{serializerType.FullName}'). "
                        + "Use a single serializer per message type."
                );
            }
        }

        var serializerByMessageType = new Dictionary<Type, IMessageSerializer>();
        foreach (var (messageType, configuredSerializerType) in serializerTypeMap)
        {
            if (configuredSerializerType == null)
                continue;

            var serializer = ResolveConfiguredSerializer(
                configuredSerializerType,
                defaultSerializer,
                serializerCandidates,
                serviceProvider
            );
            serializerByMessageType[messageType] = serializer;
        }

        try
        {
            return serializerByMessageType.ToFrozenDictionary();
        }
        catch (ArgumentException ex)
        {
            // Defensive guard to preserve a clear startup failure if map keys are duplicated.
            throw new InvalidOperationException(
                "Failed to build message serializer map due to duplicate message type registrations.",
                ex
            );
        }
    }
}
