namespace Ratatoskr.Core;

/// <summary>
/// Describes a handler registered for inbox-based durable delivery.
/// </summary>
public record InboxHandlerRegistration(
    /// <summary>Stable user-assigned key, used as the deduplication and retry key.</summary>
    string Key,
    /// <summary>CLR type of the message this handler processes.</summary>
    Type MessageType,
    /// <summary>CLR type of the handler class to resolve from DI.</summary>
    Type HandlerType,
    /// <summary>Wire type name from <see cref="RatatoskrMessageAttribute"/> (e.g. "order.created").</summary>
    string? WireTypeName);
