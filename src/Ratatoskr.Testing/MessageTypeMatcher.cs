using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Shared logic for matching message activities and tracked messages by CLR type,
/// deserialized instance, or wire type name from <see cref="RatatoskrMessageAttribute"/>.
/// </summary>
internal static class MessageTypeMatcher
{
    public static bool Matches<T>(MessageActivity activity)
    {
        var messageType = GetMessageType(activity);
        if (messageType == typeof(T))
            return true;

        var message = GetMessage(activity);
        if (message is T)
            return true;

        var typeName = GetTypeName(typeof(T));
        if (typeName != null && activity.Properties.Type == typeName)
            return true;

        return false;
    }

    public static bool Matches(MessageActivity activity, Type expectedType)
    {
        var messageType = GetMessageType(activity);
        if (messageType == expectedType)
            return true;

        var message = GetMessage(activity);
        if (message != null && expectedType.IsInstanceOfType(message))
            return true;

        var typeName = GetTypeName(expectedType);
        if (typeName != null && activity.Properties.Type == typeName)
            return true;

        return false;
    }

    public static bool Matches<T>(TrackedMessage message) => Matches<T>(message.Activity);

    public static string? GetTypeName(Type type)
    {
        var attr = type.GetCustomAttributes(typeof(RatatoskrMessageAttribute), false)
            .FirstOrDefault() as RatatoskrMessageAttribute;
        return attr?.Type;
    }

    internal static Type? GetMessageType(MessageActivity activity) => activity switch
    {
        MessagePublished a => a.MessageType,
        OutboxMessageStaged a => a.MessageType,
        MessageDispatched a => a.MessageType,
        _ => null
    };

    internal static object? GetMessage(MessageActivity activity) => activity switch
    {
        MessagePublished a => a.Message,
        OutboxMessageStaged a => a.Message,
        MessageDispatched a => a.Message,
        _ => null
    };
}
