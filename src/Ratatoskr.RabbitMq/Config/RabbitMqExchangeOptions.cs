namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// RabbitMQ exchange configuration for publish channels.
/// Only exposes exchange-related settings — queue, consumer, and retry options are not available.
/// </summary>
public class RabbitMqExchangeOptions(RabbitMqChannelOptions inner)
{
    internal RabbitMqChannelOptions Inner => inner;

    /// <summary>Configures a topic exchange with pattern-based routing.</summary>
    public RabbitMqExchangeOptions WithTopicExchange()
    {
        inner.WithTopicExchange();
        return this;
    }

    /// <summary>Configures a direct exchange with exact routing key matching.</summary>
    public RabbitMqExchangeOptions WithDirectExchange()
    {
        inner.WithDirectExchange();
        return this;
    }

    /// <summary>Configures a fanout exchange that broadcasts to all bound queues.</summary>
    public RabbitMqExchangeOptions WithFanoutExchange()
    {
        inner.WithFanoutExchange();
        return this;
    }

    /// <summary>Sets the exchange type.</summary>
    public RabbitMqExchangeOptions WithExchangeType(RabbitMqExchangeType type)
    {
        inner.WithExchangeType(type);
        return this;
    }
}
