namespace Ratatoskr.Core;

/// <summary>
/// Describes a handler registered on a specific consume channel for a specific message type.
/// </summary>
/// <param name="MessageType">CLR type of the message this handler processes.</param>
/// <param name="HandlerType">CLR type of the handler class to resolve from DI.</param>
/// <param name="IsInbox">Whether this handler is managed by the inbox processor.</param>
/// <param name="InboxKey">Stable key for inbox deduplication. Required when <paramref name="IsInbox"/> is true.</param>
/// <param name="FallbackKeys">
/// Previous handler keys that this handler should still process from existing inbox entries.
/// New inbox entries are always created with <paramref name="InboxKey"/>, not fallback keys.
/// Used during handler key renames to avoid poisoning in-flight messages.
/// </param>
public record ChannelHandlerRegistration(
    Type MessageType,
    Type HandlerType,
    bool IsInbox,
    string? InboxKey,
    IReadOnlyList<string>? FallbackKeys = null);
