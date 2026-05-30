namespace Ratatoskr.Core;

/// <summary>
/// Transport-agnostic snapshot of a message as it appears on the wire.
/// For the Sent stage, this captures the state after envelope mapping (outgoing).
/// For the Received stage, this captures the state before envelope mapping (incoming).
/// </summary>
public sealed class TransportMessageSnapshot
{
    /// <summary>
    /// The transport name the message is sent over.
    /// </summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public required string TransportName { get; init; }

    /// <summary>
    /// The raw body bytes as they appear on the wire.
    /// In structured CloudEvents mode, this is the full JSON envelope.
    /// In binary mode, this is the serialized message payload.
    /// </summary>
#pragma warning disable CA1819 // byte[] is intentional: callers use it directly with Encoding.GetString and similar APIs
    public required byte[] Body { get; init; }
#pragma warning restore CA1819

    /// <summary>
    /// Transport-level headers/properties flattened into a dictionary.
    /// For AMQP: includes standard properties (content-type, message-id, type, app-id, timestamp, delivery-mode)
    /// merged with custom headers.
    /// Byte array values from the transport are converted to UTF-8 strings for ergonomic assertions.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Headers { get; init; }

    /// <summary>
    /// Transport-specific delivery/routing metadata that is not part of the message headers.
    /// For AMQP outgoing: exchange, routing-key.
    /// For AMQP incoming: exchange, routing-key, redelivered.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Metadata { get; init; }
}
