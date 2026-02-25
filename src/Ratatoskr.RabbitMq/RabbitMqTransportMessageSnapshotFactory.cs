using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Creates <see cref="TransportMessageSnapshot"/> instances from RabbitMQ types,
/// flattening standard AMQP properties and custom headers into a single dictionary.
/// </summary>
internal static class RabbitMqTransportMessageSnapshotFactory
{
    /// <summary>
    /// Creates a TransportMessage from outgoing BasicProperties after envelope mapping.
    /// Used at the Sent stage to capture the wire format.
    /// </summary>
    public static TransportMessageSnapshot FromBasicProperties(
        IReadOnlyBasicProperties props, byte[] body, string exchange, string routingKey)
    {
        var headers = BuildHeaders(props);

        var metadata = new Dictionary<string, object?>
        {
            ["exchange"] = exchange,
            ["routing-key"] = routingKey,
        };

        return new TransportMessageSnapshot { Body = body, Headers = headers, Metadata = metadata };
    }

    /// <summary>
    /// Creates a TransportMessage from incoming BasicDeliverEventArgs before envelope mapping.
    /// Used at the Received stage to capture the raw wire data.
    /// </summary>
    public static TransportMessageSnapshot FromDeliverEventArgs(BasicDeliverEventArgs ea)
    {
        var headers = BuildHeaders(ea.BasicProperties);

        var metadata = new Dictionary<string, object?>
        {
            ["exchange"] = ea.Exchange,
            ["routing-key"] = ea.RoutingKey,
            ["redelivered"] = ea.Redelivered,
        };

        return new TransportMessageSnapshot { Body = ea.Body.ToArray(), Headers = headers, Metadata = metadata };
    }

    private static Dictionary<string, object?> BuildHeaders(IReadOnlyBasicProperties props)
    {
        var headers = new Dictionary<string, object?>();

        // Standard AMQP properties
        if (props.ContentType != null)
            headers["content-type"] = props.ContentType;
        if (props.MessageId != null)
            headers["message-id"] = props.MessageId;
        if (props.Type != null)
            headers["type"] = props.Type;
        if (props.AppId != null)
            headers["app-id"] = props.AppId;
        if (props.Timestamp.UnixTime > 0)
            headers["timestamp"] = props.Timestamp.UnixTime;
        if (props.IsDeliveryModePresent())
            headers["delivery-mode"] = (int)props.DeliveryMode;

        // Custom headers (standard AMQP properties take precedence on collision)
        if (props.Headers != null)
        {
            foreach (var (key, value) in props.Headers)
            {
                headers.TryAdd(key, NormalizeValue(value));
            }
        }

        return headers;
    }

    /// <summary>
    /// Normalizes header values for ergonomic assertions.
    /// Converts byte arrays (common in AMQP) to UTF-8 strings when they contain valid UTF-8.
    /// Returns the original byte[] for non-UTF-8 binary data.
    /// </summary>
    private static object? NormalizeValue(object? value) => value switch
    {
        byte[] bytes => TryDecodeUtf8(bytes),
        _ => value
    };

    private static object TryDecodeUtf8(byte[] bytes)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(bytes);
            // Verify round-trip: re-encode and compare to detect replacement characters
            if (Encoding.UTF8.GetBytes(decoded).AsSpan().SequenceEqual(bytes))
                return decoded;
            return bytes;
        }
        catch (DecoderFallbackException)
        {
            return bytes;
        }
    }
}
