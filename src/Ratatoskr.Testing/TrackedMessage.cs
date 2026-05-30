using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Rich wrapper around a <see cref="MessageActivity"/> providing typed access
/// to the captured message and its metadata.
/// </summary>
public class TrackedMessage
{
    internal TrackedMessage(MessageActivity activity) => Activity = activity;

    internal MessageActivity Activity { get; }

    /// <summary>
    /// The message properties at the time of capture.
    /// </summary>
    public MessageProperties Properties => Activity.Properties;

    /// <summary>
    /// The raw serialized body bytes. At the Sent stage, this is the exact bytes on the wire.
    /// </summary>
#pragma warning disable CA1819 // byte[] is intentional: callers use it directly with Encoding.GetString and similar APIs
    public byte[]? RawBody => Activity.SerializedBody;
#pragma warning restore CA1819

    /// <summary>
    /// The dispatch result. Only set at the Dispatched stage.
    /// </summary>
    public DispatchResult? Result => Activity.DispatchResult;

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
    public Exception? Exception => Activity.Exception;

    /// <summary>
    /// The CLR type of the message, if available.
    /// </summary>
    public Type? MessageType => Activity.MessageType;

    /// <summary>
    /// The transport-level wire representation of the message.
    /// Available at Sent (after envelope mapping) and Received (before envelope mapping) stages.
    /// </summary>
    public TransportMessageSnapshot? TransportMessage => Activity.TransportMessage;

    /// <summary>
    /// Whether the operation succeeded. Set at the InboxDispatched stage.
    /// </summary>
    public bool? IsSuccess => Activity.IsSuccess;

    /// <summary>
    /// The transport that delivered this message (e.g. "rabbitmq", "efcore").
    /// Set at InboxQueued and InboxDispatched stages.
    /// </summary>
    public string? TransportName => Activity.TransportName;

    /// <summary>
    /// The trace ID extracted from the message's TraceParent header.
    /// </summary>
    public string? TraceId => MessageTracker.ExtractTraceId(Activity.Properties.TraceParent);

    /// <summary>
    /// Gets the deserialized message as the specified type.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the message is not available or not of the expected type.</exception>
    public T GetMessage<T>()
        where T : notnull
    {
        if (Activity.Message == null)
        {
            throw new InvalidOperationException(
                $"Message object is not available at the {Stage} stage. "
                    + "Deserialized messages are available at Published, OutboxStaged, and Dispatched stages."
            );
        }

        if (Activity.Message is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            $"Message is of type {Activity.Message.GetType().Name}, not {typeof(T).Name}."
        );
    }
}
