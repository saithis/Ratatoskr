namespace Ratatoskr.Core;

/// <summary>
/// Defines the stages in the message lifecycle where activities can be observed.
/// </summary>
public enum MessageStage
{
    /// <summary>
    /// IRatatoskr.PublishDirectAsync completed - message enriched and serialized.
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
    /// Any exception that occurred. Set when dispatch fails.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// When this activity was captured.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The transport-level wire representation of the message.
    /// Present for Sent and Received stages where transport data is available.
    /// For Sent: captured after envelope mapping (what was published to the transport).
    /// For Received: captured before envelope mapping (what arrived from the transport).
    /// </summary>
    public TransportMessage? TransportMessage { get; init; }
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
