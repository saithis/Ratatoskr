namespace Ratatoskr.Core;

/// <summary>
/// Describes a handler registered on a specific consume channel for a specific message type.
/// </summary>
/// <param name="MessageType">CLR type of the message this handler processes.</param>
/// <param name="HandlerType">CLR type of the handler class to resolve from DI.</param>
/// <param name="IsInbox">Whether this handler is managed by the inbox processor.</param>
/// <param name="InboxKey">Stable key for inbox deduplication. Required when <paramref name="IsInbox"/> is true.</param>
/// <param name="IsExplicitFireAndForget">True when the handler was explicitly opted out of inbox via <c>WithoutInbox()</c>.</param>
public record ChannelHandlerRegistration(
    Type MessageType,
    Type HandlerType,
    bool IsInbox,
    string? InboxKey,
    bool IsExplicitFireAndForget = false);
