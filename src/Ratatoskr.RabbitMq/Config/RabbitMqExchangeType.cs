using RabbitMQ.Client;
using Ratatoskr.AsyncApi.Model.Bindings;

namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// The type of AMQP exchange. Maps to RabbitMQ exchange types.
/// </summary>
public enum RabbitMqExchangeType
{
    /// <summary>Topic exchange with pattern-based routing (e.g. "order.*").</summary>
    Topic = 0,

    /// <summary>Direct exchange with exact routing key matching.</summary>
    Direct = 1,

    /// <summary>Fanout exchange that broadcasts to all bound queues.</summary>
    Fanout = 2,

    /// <summary>Headers exchange that routes based on message headers.</summary>
    Headers = 3,
}

internal static class RabbitMqExchangeTypeExtensions
{
    /// <summary>
    /// Converts to the string value expected by the RabbitMQ client library.
    /// </summary>
    internal static string ToRabbitMqString(this RabbitMqExchangeType type) =>
        type switch
        {
            RabbitMqExchangeType.Topic => ExchangeType.Topic,
            RabbitMqExchangeType.Direct => ExchangeType.Direct,
            RabbitMqExchangeType.Fanout => ExchangeType.Fanout,
            RabbitMqExchangeType.Headers => ExchangeType.Headers,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, message: null),
        };

    /// <summary>
    /// Converts to the AsyncAPI AMQP binding exchange type.
    /// </summary>
    internal static AmqpExchangeType ToAmqpExchangeType(this RabbitMqExchangeType type) =>
        type switch
        {
            RabbitMqExchangeType.Topic => AmqpExchangeType.Topic,
            RabbitMqExchangeType.Direct => AmqpExchangeType.Direct,
            RabbitMqExchangeType.Fanout => AmqpExchangeType.Fanout,
            RabbitMqExchangeType.Headers => AmqpExchangeType.Headers,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, message: null),
        };
}
