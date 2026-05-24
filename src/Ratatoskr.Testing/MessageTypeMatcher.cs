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
        if (activity.MessageType == typeof(T))
        {
            return true;
        }

        if (activity.Message is T)
        {
            return true;
        }

        var typeName = GetTypeName(typeof(T));
        if (typeName != null && activity.Properties.Type == typeName)
        {
            return true;
        }

        return false;
    }

    public static bool Matches(MessageActivity activity, Type expectedType)
    {
        if (activity.MessageType == expectedType)
        {
            return true;
        }

        if (activity.Message != null && expectedType.IsInstanceOfType(activity.Message))
        {
            return true;
        }

        var typeName = GetTypeName(expectedType);
        if (typeName != null && activity.Properties.Type == typeName)
        {
            return true;
        }

        return false;
    }

    public static bool Matches<T>(TrackedMessage message) => Matches<T>(message.Activity);

    public static string? GetTypeName(Type type)
    {
        var attr =
            type.GetCustomAttributes(typeof(RatatoskrMessageAttribute), false).FirstOrDefault()
            as RatatoskrMessageAttribute;
        return attr?.Type;
    }
}
