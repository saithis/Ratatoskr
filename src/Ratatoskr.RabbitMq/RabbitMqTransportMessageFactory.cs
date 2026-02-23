using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ratatoskr.Core;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Creates <see cref="TransportMessage"/> instances from RabbitMQ types,
/// flattening standard AMQP properties and custom headers into a single dictionary.
/// </summary>
internal static class RabbitMqTransportMessageFactory
{
    /// <summary>
    /// Creates a TransportMessage from outgoing BasicProperties after envelope mapping.
    /// Used at the Sent stage to capture the wire format.
    /// </summary>
    public static TransportMessage FromBasicProperties(
        BasicProperties props, byte[] body, string exchange, string routingKey)
    {
        var headers = BuildHeaders(props);

        var metadata = new Dictionary<string, object?>
        {
            ["exchange"] = exchange,
            ["routing-key"] = routingKey,
        };

        return new TransportMessage { Body = body, Headers = headers, Metadata = metadata };
    }

    /// <summary>
    /// Creates a TransportMessage from incoming BasicDeliverEventArgs before envelope mapping.
    /// Used at the Received stage to capture the raw wire data.
    /// </summary>
    public static TransportMessage FromDeliverEventArgs(BasicDeliverEventArgs ea)
    {
        var headers = BuildHeaders(ea.BasicProperties);

        var metadata = new Dictionary<string, object?>
        {
            ["exchange"] = ea.Exchange,
            ["routing-key"] = ea.RoutingKey,
            ["redelivered"] = ea.Redelivered,
        };

        return new TransportMessage { Body = ea.Body.ToArray(), Headers = headers, Metadata = metadata };
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
        if (props.DeliveryMode > 0)
            headers["delivery-mode"] = (int)props.DeliveryMode;

        // Custom headers
        if (props.Headers != null)
        {
            foreach (var (key, value) in props.Headers)
            {
                headers[key] = NormalizeValue(value);
            }
        }

        return headers;
    }

    /// <summary>
    /// Normalizes header values for ergonomic assertions.
    /// Converts byte arrays (common in AMQP) to UTF-8 strings.
    /// </summary>
    private static object? NormalizeValue(object? value) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => value
    };
}
