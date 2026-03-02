using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Rich wrapper around a <see cref="MessageActivity"/> providing typed access
/// to the captured message and its metadata.
/// </summary>
public class TrackedMessage
{
    internal TrackedMessage(MessageActivity activity)
    {
        Activity = activity;
    }

    internal MessageActivity Activity { get; }

    /// <summary>
    /// The message properties at the time of capture.
    /// </summary>
    public MessageProperties Properties => Activity.Properties;

    /// <summary>
    /// The raw serialized body bytes. At the Sent stage, this is the exact bytes on the wire.
    /// </summary>
    public byte[] RawBody => Activity.SerializedBody;

    /// <summary>
    /// The dispatch result. Only set at the Dispatched stage.
    /// </summary>
    public DispatchResult? Result => Activity is MessageDispatched d ? d.DispatchResult : null;

    /// <summary>
    /// The pipeline stage where this message was captured.
    /// </summary>
    public MessageStage Stage => Activity.Stage;

    /// <summary>
    /// When this activity was captured.
    /// </summary>
    public DateTimeOffset Timestamp => Activity.Timestamp;

    /// <summary>
    /// Any exception that occurred during processing.
    /// </summary>
    public Exception? Exception => Activity switch
    {
        MessagePublished a => a.Exception,
        MessageSent a => a.Exception,
        MessageDispatched a => a.Exception,
        InboxMessageDispatched a => a.Exception,
        InboxMessagePoisoned a => a.Exception,
        _ => null
    };

    /// <summary>
    /// The CLR type of the message, if available.
    /// </summary>
    public Type? MessageType => MessageTypeMatcher.GetMessageType(Activity);

    /// <summary>
    /// The transport-level wire representation of the message.
    /// Available at Sent (after envelope mapping) and Received (before envelope mapping) stages.
    /// </summary>
    public TransportMessageSnapshot? TransportMessage => Activity switch
    {
        MessageSent a => a.TransportMessage,
        MessageReceived a => a.TransportMessage,
        _ => null
    };

    /// <summary>
    /// Whether the operation succeeded. Set at the InboxDispatched stage.
    /// </summary>
    public bool? IsSuccess => Activity is InboxMessageDispatched d ? d.IsSuccess : null;

    /// <summary>
    /// The transport that delivered this message (e.g. "rabbitmq", "local").
    /// </summary>
    public string? TransportName => Activity switch
    {
        MessagePublished a => a.TransportName,
        MessageSent a => a.TransportName,
        OutboxMessageSent a => a.TransportName,
        MessageReceived a => a.TransportName,
        InboxMessageQueued a => a.TransportName,
        InboxMessageDispatched a => a.TransportName,
        InboxMessagePoisoned a => a.TransportName,
        _ => null
    };

    /// <summary>
    /// The trace ID extracted from the message's TraceParent header.
    /// </summary>
    public string? TraceId => MessageTracker.ExtractTraceId(Activity.Properties.TraceParent);

    /// <summary>
    /// Casts the underlying activity to a specific stage type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the activity is not of the expected type.</exception>
    public T As<T>() where T : MessageActivity =>
        Activity as T ?? throw new InvalidOperationException(
            $"Activity is {Activity.GetType().Name}, not {typeof(T).Name}.");

    /// <summary>
    /// Gets the deserialized message as the specified type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the message is not available or not of the expected type.</exception>
    public T GetMessage<T>() where T : notnull
    {
        var message = MessageTypeMatcher.GetMessage(Activity);

        if (message is T typed)
            return typed;

        if (message == null)
            throw new InvalidOperationException(
                $"Message object is not available at the {Activity.GetType().Name} stage. " +
                "Deserialized messages are available at Published, OutboxStaged, and Dispatched stages.");

        throw new InvalidOperationException(
            $"Message is of type {message.GetType().Name}, not {typeof(T).Name}.");
    }
}
