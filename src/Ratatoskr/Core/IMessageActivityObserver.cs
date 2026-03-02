namespace Ratatoskr.Core;

/// <summary>
/// Defines the stages in the message lifecycle where activities can be observed.
/// </summary>
public enum MessageStage
{
    /// <summary>
    /// Fired once per transport after each IMessageSender.SendAsync attempt during PublishDirectAsync.
    /// Includes TransportName and Exception (null on success) so observers can see per-transport outcomes.
    /// </summary>
    Published,

    /// <summary>
    /// IMessageSender.SendAsync completed - bytes are on the wire.
    /// </summary>
    Sent,

    /// <summary>
    /// Message serialized into outbox entity during SaveChanges.
    /// </summary>
    OutboxStaged,

    /// <summary>
    /// Outbox processor sent message to transport.
    /// </summary>
    OutboxSent,

    /// <summary>
    /// Consumer received message from transport, before dispatch.
    /// </summary>
    Received,

    /// <summary>
    /// MessageDispatcher completed handler invocation.
    /// </summary>
    Dispatched,

    /// <summary>
    /// Message accepted into the inbox (persisted to DB). Inbox-managed handlers will be
    /// invoked asynchronously by the InboxProcessor.
    /// </summary>
    InboxQueued,

    /// <summary>
    /// InboxProcessor completed invocation of a single inbox-managed handler.
    /// Fired once per handler per delivery attempt.
    /// </summary>
    InboxDispatched,

    /// <summary>
    /// A handler status has been marked as poisoned after exceeding the maximum retry count.
    /// </summary>
    InboxPoisoned,
}

/// <summary>
/// Abstract base for all message activity records. Each pipeline stage has its own
/// sealed record subtype with exactly the properties relevant to that stage.
/// Use pattern matching to access stage-specific data.
/// </summary>
public abstract record MessageActivity
{
    /// <summary>
    /// The message properties at the time of capture.
    /// </summary>
    public required MessageProperties Properties { get; init; }

    /// <summary>
    /// When this activity was captured.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The serialized message body (raw bytes).
    /// At the Sent stage, this is the exact bytes sent to the transport (including CloudEvents envelope in structured mode).
    /// </summary>
    public required byte[] SerializedBody { get; init; }

    /// <summary>
    /// The pipeline stage, derived from the concrete record type.
    /// </summary>
    public MessageStage Stage => this switch
    {
        MessagePublished => MessageStage.Published,
        MessageSent => MessageStage.Sent,
        OutboxMessageStaged => MessageStage.OutboxStaged,
        OutboxMessageSent => MessageStage.OutboxSent,
        MessageReceived => MessageStage.Received,
        MessageDispatched => MessageStage.Dispatched,
        InboxMessageQueued => MessageStage.InboxQueued,
        InboxMessageDispatched => MessageStage.InboxDispatched,
        InboxMessagePoisoned => MessageStage.InboxPoisoned,
        _ => throw new InvalidOperationException($"Unknown activity type: {GetType().Name}")
    };
}

/// <summary>
/// Fired once per transport after each IMessageSender.SendAsync attempt during PublishDirectAsync.
/// </summary>
public sealed record MessagePublished : MessageActivity
{
    /// <summary>
    /// The transport this publish attempt targeted (e.g. "rabbitmq", "local").
    /// </summary>
    public required string TransportName { get; init; }

    /// <summary>
    /// The deserialized message object.
    /// </summary>
    public required object Message { get; init; }

    /// <summary>
    /// The CLR type of the message.
    /// </summary>
    public required Type MessageType { get; init; }

    /// <summary>
    /// The exception if the send failed for this transport; null on success.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Fired inside each IMessageSender.SendAsync after bytes are on the wire.
/// </summary>
public sealed record MessageSent : MessageActivity
{
    /// <summary>
    /// The transport that sent the message (e.g. "rabbitmq", "local").
    /// </summary>
    public required string TransportName { get; init; }

    /// <summary>
    /// The transport-level wire representation.
    /// Captured after envelope mapping (what was published to the transport).
    /// </summary>
    public required TransportMessageSnapshot TransportMessage { get; init; }

    /// <summary>
    /// The exception if the send failed; null on success.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Fired once per message when serialized into an outbox entity during SaveChanges.
/// </summary>
public sealed record OutboxMessageStaged : MessageActivity
{
    /// <summary>
    /// The deserialized message object.
    /// </summary>
    public required object Message { get; init; }

    /// <summary>
    /// The CLR type of the message.
    /// </summary>
    public required Type MessageType { get; init; }
}

/// <summary>
/// Fired once per outbox row when the outbox processor successfully sends to a transport.
/// </summary>
public sealed record OutboxMessageSent : MessageActivity
{
    /// <summary>
    /// The transport the outbox message was sent to.
    /// </summary>
    public required string TransportName { get; init; }
}

/// <summary>
/// Fired once per transport when a consumer receives a message, before dispatch.
/// </summary>
public sealed record MessageReceived : MessageActivity
{
    /// <summary>
    /// The transport that received the message (e.g. "rabbitmq", "local").
    /// </summary>
    public required string TransportName { get; init; }

    /// <summary>
    /// The transport-level wire representation.
    /// Captured before envelope mapping (what arrived from the transport).
    /// </summary>
    public required TransportMessageSnapshot TransportMessage { get; init; }
}

/// <summary>
/// Fired once after MessageDispatcher completes handler invocation for a message.
/// </summary>
public sealed record MessageDispatched : MessageActivity
{
    /// <summary>
    /// The deserialized message object.
    /// </summary>
    public required object Message { get; init; }

    /// <summary>
    /// The CLR type of the message.
    /// </summary>
    public required Type MessageType { get; init; }

    /// <summary>
    /// The aggregate dispatch result across all handlers.
    /// </summary>
    public required DispatchResult DispatchResult { get; init; }

    /// <summary>
    /// The exception if dispatch failed; null on success.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Fired once when a message is accepted into the inbox (persisted to DB).
/// Inbox-managed handlers will be invoked asynchronously by the InboxProcessor.
/// </summary>
public sealed record InboxMessageQueued : MessageActivity
{
    /// <summary>
    /// The transport that delivered the message.
    /// </summary>
    public required string TransportName { get; init; }
}

/// <summary>
/// Fired once per handler per delivery attempt by the InboxProcessor.
/// </summary>
public sealed record InboxMessageDispatched : MessageActivity
{
    /// <summary>
    /// The transport that originally delivered the message.
    /// </summary>
    public required string TransportName { get; init; }

    /// <summary>
    /// Whether the handler invocation succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// The exception if the handler failed; null on success.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Fired when a handler status is marked as poisoned after exceeding the maximum retry count.
/// </summary>
public sealed record InboxMessagePoisoned : MessageActivity
{
    /// <summary>
    /// The transport that originally delivered the message.
    /// </summary>
    public required string TransportName { get; init; }

    /// <summary>
    /// The exception from the final failed attempt.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Observer interface for monitoring message activities in the pipeline.
/// Implementations are called at various stages of message processing.
/// When no observers are registered, the pipeline has zero overhead.
/// </summary>
public interface IMessageActivityObserver
{
    /// <summary>
    /// Called when a message activity occurs at any pipeline stage.
    /// Use pattern matching on the <paramref name="activity"/> to access stage-specific properties.
    /// </summary>
    ValueTask OnMessageActivity(MessageActivity activity);
}
