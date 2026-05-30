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
        var typeName = GetTypeName(typeof(T));
        return activity.MessageType == typeof(T)
            || activity.Message is T
            || (typeName != null && activity.Properties.Type == typeName);
    }

    public static bool Matches(MessageActivity activity, Type expectedType)
    {
        var typeName = GetTypeName(expectedType);
        return activity.MessageType == expectedType
            || (activity.Message != null && expectedType.IsInstanceOfType(activity.Message))
            || (typeName != null && activity.Properties.Type == typeName);
    }

    public static bool Matches<T>(TrackedMessage message) => Matches<T>(message.Activity);

    public static string? GetTypeName(Type type)
    {
        var attr =
            type.GetCustomAttributes(typeof(RatatoskrMessageAttribute), inherit: false)
                .FirstOrDefault() as RatatoskrMessageAttribute;
        return attr?.Type;
    }
}
