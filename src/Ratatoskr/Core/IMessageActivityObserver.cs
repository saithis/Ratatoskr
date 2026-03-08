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
/// Represents an observed message activity at a specific pipeline stage.
/// </summary>
public class MessageActivity
{
    /// <summary>
    /// The pipeline stage where this activity was captured.
    /// </summary>
    public required MessageStage Stage { get; init; }

    /// <summary>
    /// The message properties at the time of capture.
    /// </summary>
    public required MessageProperties Properties { get; init; }

    /// <summary>
    /// The serialized message body (raw bytes). May be null for some stages.
    /// At the Sent stage, this is the exact bytes sent to the transport (including CloudEvents envelope in structured mode).
    /// </summary>
    public byte[]? SerializedBody { get; init; }

    /// <summary>
    /// The deserialized message object. Available at Published, OutboxStaged, and Dispatched stages.
    /// </summary>
    public object? Message { get; init; }

    /// <summary>
    /// The CLR type of the message.
    /// </summary>
    public Type? MessageType { get; init; }

    /// <summary>
    /// The dispatch result. Only set at the Dispatched stage.
    /// </summary>
    public DispatchResult? DispatchResult { get; init; }

    /// <summary>
    /// Whether the operation succeeded. Set at the <see cref="MessageStage.InboxDispatched"/> stage:
    /// <c>true</c> on success, <c>false</c> on failure. <c>null</c> for other stages.
    /// </summary>
    public bool? IsSuccess { get; init; }

    /// <summary>
    /// Any exception that occurred. Set when dispatch fails.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// When this activity was captured.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The transport name this activity relates to (e.g. "rabbitmq", "efcore").
    /// Set at Published, Sent, Received, and OutboxSent stages where a specific transport is involved.
    /// </summary>
    public string? TransportName { get; init; }

    /// <summary>
    /// The transport-level wire representation of the message.
    /// Present for Sent and Received stages where transport data is available.
    /// For Sent: captured after envelope mapping (what was published to the transport).
    /// For Received: captured before envelope mapping (what arrived from the transport).
    /// </summary>
    public TransportMessageSnapshot? TransportMessage { get; init; }
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
    /// </summary>
    ValueTask OnMessageActivity(MessageActivity activity);
}
