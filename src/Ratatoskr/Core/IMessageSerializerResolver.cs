namespace Ratatoskr.Core;

/// <summary>
/// Resolves the serializer to use for a message type.
/// Falls back to the global <see cref="IMessageSerializer"/> when no message-specific serializer is configured.
/// </summary>
public interface IMessageSerializerResolver
{
    public IMessageSerializer GetSerializer(Type messageType);
}
