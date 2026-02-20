using Ratatoskr.AsyncApi.Model.Bindings;

namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// The type of AMQP exchange. Maps to RabbitMQ exchange types.
/// </summary>
public enum ExchangeType
{
    /// <summary>Topic exchange with pattern-based routing (e.g. "order.*").</summary>
    Topic,

    /// <summary>Direct exchange with exact routing key matching.</summary>
    Direct,

    /// <summary>Fanout exchange that broadcasts to all bound queues.</summary>
    Fanout,

    /// <summary>Headers exchange that routes based on message headers.</summary>
    Headers
}

internal static class ExchangeTypeExtensions
{
    /// <summary>
    /// Converts to the string value expected by the RabbitMQ client library.
    /// </summary>
    internal static string ToRabbitMqString(this ExchangeType type) => type switch
    {
        ExchangeType.Topic => "topic",
        ExchangeType.Direct => "direct",
        ExchangeType.Fanout => "fanout",
        ExchangeType.Headers => "headers",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>
    /// Converts to the AsyncAPI AMQP binding exchange type.
    /// </summary>
    internal static AmqpExchangeType ToAmqpExchangeType(this ExchangeType type) => type switch
    {
        ExchangeType.Topic => AmqpExchangeType.Topic,
        ExchangeType.Direct => AmqpExchangeType.Direct,
        ExchangeType.Fanout => AmqpExchangeType.Fanout,
        ExchangeType.Headers => AmqpExchangeType.Headers,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
