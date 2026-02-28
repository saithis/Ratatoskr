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
    /// <summary>Wire type name (e.g. "order.created") — from config or [RatatoskrMessage] attribute.</summary>
    string? WireTypeName)
{
    /// <summary>
    /// Compiled delegate for invoking <see cref="IMessageHandler{T}.HandleAsync"/> without per-call reflection.
    /// </summary>
    public Func<object, object, MessageProperties, CancellationToken, Task> Invoke { get; } =
        HandlerInvokerCache.Get(MessageType);
}
