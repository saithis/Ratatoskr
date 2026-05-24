namespace Ratatoskr.Core;

/// <summary>
/// Describes a handler registered on a specific consume channel for a specific message type.
/// </summary>
public record ChannelHandlerRegistration
{
    /// <summary>Gets the CLR type of the message this handler processes.</summary>
    public required Type MessageType { get; init; }

    /// <summary>Gets the CLR type of the handler class to resolve from DI.</summary>
    public required Type HandlerType { get; init; }

    /// <summary>Gets a value indicating whether this handler is managed by the inbox processor.</summary>
    public required bool IsInbox { get; init; }

    /// <summary>Gets the stable key for inbox deduplication. Required when IsInbox is true.</summary>
    public required string? InboxKey { get; init; }

    /// <summary>Gets the previous handler keys that should still be matched when processing existing inbox entries during a handler rename transition.</summary>
    /// <remarks>Legacy keys are never used to create new inbox entries.</remarks>
    public IReadOnlyList<string> LegacyKeys { get; init; } = Array.Empty<string>();
}
