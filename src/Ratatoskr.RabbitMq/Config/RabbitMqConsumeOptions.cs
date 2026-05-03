namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// RabbitMQ configuration for consume channels.
/// Exposes exchange, queue, consumer, and retry settings.
/// </summary>
public class RabbitMqConsumeOptions(RabbitMqChannelOptions inner)
{
    internal RabbitMqChannelOptions Inner => inner;

    // ── Exchange ──────────────────────────────────────────────────────

    /// <summary>Configures a topic exchange with pattern-based routing.</summary>
    public RabbitMqConsumeOptions WithTopicExchange()
    {
        inner.WithTopicExchange();
        return this;
    }

    /// <summary>Configures a direct exchange with exact routing key matching.</summary>
    public RabbitMqConsumeOptions WithDirectExchange()
    {
        inner.WithDirectExchange();
        return this;
    }

    /// <summary>Configures a fanout exchange that broadcasts to all bound queues.</summary>
    public RabbitMqConsumeOptions WithFanoutExchange()
    {
        inner.WithFanoutExchange();
        return this;
    }

    /// <summary>Sets the exchange type.</summary>
    public RabbitMqConsumeOptions WithExchangeType(RabbitMqExchangeType type)
    {
        inner.WithExchangeType(type);
        return this;
    }

    /// <summary>Sets whether the exchange survives broker restarts.</summary>
    public RabbitMqConsumeOptions WithExchangeDurable(bool durable = true)
    {
        inner.WithExchangeDurable(durable);
        return this;
    }

    /// <summary>Sets whether the exchange is deleted when the last queue is unbound.</summary>
    public RabbitMqConsumeOptions WithExchangeAutoDelete(bool autoDelete = true)
    {
        inner.WithExchangeAutoDelete(autoDelete);
        return this;
    }

    // ── Queue / Consumer ─────────────────────────────────────────────

    /// <summary>Sets the queue name. Required for consume channels.</summary>
    public RabbitMqConsumeOptions WithQueueName(string name)
    {
        inner.WithQueueName(name);
        return this;
    }

    /// <summary>
    /// Binds this consumer to the given AMQP exchange (when it differs from the Ratatoskr channel name).
    /// </summary>
    public RabbitMqConsumeOptions WithAmqpExchangeName(string exchangeName)
    {
        inner.WithAmqpExchangeName(exchangeName);
        return this;
    }

    /// <summary>Sets the prefetch count (max unacknowledged messages per consumer).</summary>
    public RabbitMqConsumeOptions WithPrefetch(ushort count)
    {
        inner.WithPrefetch(count);
        return this;
    }

    /// <summary>Sets the maximum number of handlers that can run concurrently.</summary>
    public RabbitMqConsumeOptions WithConcurrencyLimit(ushort concurrencyLimit)
    {
        inner.WithConcurrencyLimit(concurrencyLimit);
        return this;
    }

    /// <summary>Enables or disables auto-acknowledgement of messages.</summary>
    public RabbitMqConsumeOptions WithAutoAck(bool autoAck = true)
    {
        inner.WithAutoAck(autoAck);
        return this;
    }

    /// <summary>
    /// Configures a durable queue (survives broker restarts, not exclusive, not auto-deleted).
    /// This is the default queue configuration.
    /// </summary>
    public RabbitMqConsumeOptions WithDurableQueue()
    {
        inner.WithDurableQueue();
        return this;
    }

    /// <summary>
    /// Configures a transient queue (non-durable, not exclusive, auto-deleted when empty).
    /// Suitable for temporary or test queues.
    /// </summary>
    public RabbitMqConsumeOptions WithTransientQueue()
    {
        inner.WithTransientQueue();
        return this;
    }

    /// <summary>Sets the queue implementation type (Classic or Quorum).</summary>
    public RabbitMqConsumeOptions WithQueueType(QueueType type)
    {
        inner.WithQueueType(type);
        return this;
    }

    /// <summary>Sets additional queue arguments passed to RabbitMQ on declaration.</summary>
    public RabbitMqConsumeOptions WithQueueArguments(IDictionary<string, object?> arguments)
    {
        inner.WithQueueArguments(arguments);
        return this;
    }

    // ── Retry ────────────────────────────────────────────────────────

    /// <summary>Configures retry behavior using a builder callback.</summary>
    public RabbitMqConsumeOptions WithRetry(Action<RetryOptions> configure)
    {
        inner.WithRetry(configure);
        return this;
    }

    /// <summary>Configures retry behavior with max retries and optional delay.</summary>
    public RabbitMqConsumeOptions WithRetry(int maxRetries, TimeSpan? delay = null)
    {
        inner.WithRetry(maxRetries, delay);
        return this;
    }
}
