namespace Ratatoskr.RabbitMq.Config;

/// <summary>
/// Unified RabbitMQ configuration for a channel, covering exchange, queue, consumer, and retry settings.
/// Use the fluent <c>With*</c> methods to configure.
/// </summary>
public class RabbitMqChannelOptions
{
    // ── Exchange ──────────────────────────────────────────────────────

    /// <summary>
    /// The AMQP exchange type. Default: <see cref="RabbitMqExchangeType.Topic"/>.
    /// </summary>
    public RabbitMqExchangeType ExchangeType { get; private set; } = RabbitMqExchangeType.Topic;

    /// <summary>
    /// Whether the exchange survives broker restarts. Default: true.
    /// </summary>
    public bool ExchangeDurable { get; private set; } = true;

    /// <summary>
    /// Whether the exchange is deleted when the last queue is unbound. Default: false.
    /// </summary>
    public bool ExchangeAutoDelete { get; private set; } = false;

    // ── Queue / Consumer ─────────────────────────────────────────────

    /// <summary>
    /// The name of the queue to consume from. Required for consume channels.
    /// </summary>
    public string? QueueName { get; private set; }

    /// <summary>
    /// Maximum number of unacknowledged messages delivered to this consumer.
    /// Lower values reduce memory usage but may decrease throughput. Default: 10.
    /// </summary>
    public ushort PrefetchCount { get; private set; } = 10;

    /// <summary>
    /// Whether the broker should auto-acknowledge messages on delivery.
    /// When true, messages cannot be retried on failure. Default: false.
    /// </summary>
    public bool AutoAck { get; private set; } = false;

    /// <summary>
    /// Whether the queue survives broker restarts. Default: true.
    /// </summary>
    public bool QueueDurable { get; private set; } = true;

    /// <summary>
    /// Whether the queue is exclusive to this connection. Default: false.
    /// </summary>
    public bool QueueExclusive { get; private set; } = false;

    /// <summary>
    /// Whether the queue is deleted when the last consumer disconnects. Default: false.
    /// Not supported with <see cref="Config.QueueType.Quorum"/> queues.
    /// </summary>
    public bool QueueAutoDelete { get; private set; } = false;

    /// <summary>
    /// The queue implementation type. Default: <see cref="Config.QueueType.Quorum"/>.
    /// </summary>
    public QueueType QueueType { get; private set; } = QueueType.Quorum;

    /// <summary>
    /// Additional queue arguments passed to RabbitMQ on queue declaration.
    /// </summary>
    public IDictionary<string, object?> QueueArguments { get; private set; } = new Dictionary<string, object?>();

    // ── Retry ────────────────────────────────────────────────────────

    /// <summary>
    /// Retry and dead-letter configuration for failed message processing.
    /// </summary>
    public RetryOptions Retry { get; } = new();

    // ── Fluent API: Exchange ─────────────────────────────────────────

    /// <summary>Configures a topic exchange with pattern-based routing.</summary>
    public RabbitMqChannelOptions WithTopicExchange()
    {
        ExchangeType = RabbitMqExchangeType.Topic;
        return this;
    }

    /// <summary>Configures a direct exchange with exact routing key matching.</summary>
    public RabbitMqChannelOptions WithDirectExchange()
    {
        ExchangeType = RabbitMqExchangeType.Direct;
        return this;
    }

    /// <summary>Configures a fanout exchange that broadcasts to all bound queues.</summary>
    public RabbitMqChannelOptions WithFanoutExchange()
    {
        ExchangeType = RabbitMqExchangeType.Fanout;
        return this;
    }

    /// <summary>Sets the exchange type.</summary>
    public RabbitMqChannelOptions WithExchangeType(RabbitMqExchangeType type)
    {
        ExchangeType = type;
        return this;
    }

    /// <summary>Sets whether the exchange survives broker restarts.</summary>
    public RabbitMqChannelOptions WithExchangeDurable(bool durable = true)
    {
        ExchangeDurable = durable;
        return this;
    }

    /// <summary>Sets whether the exchange is deleted when the last queue is unbound.</summary>
    public RabbitMqChannelOptions WithExchangeAutoDelete(bool autoDelete = true)
    {
        ExchangeAutoDelete = autoDelete;
        return this;
    }

    // ── Fluent API: Queue ────────────────────────────────────────────

    /// <summary>Sets the queue name. Required for consume channels.</summary>
    public RabbitMqChannelOptions WithQueueName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        QueueName = name;
        return this;
    }

    /// <summary>Sets the prefetch count (max unacknowledged messages per consumer).</summary>
    public RabbitMqChannelOptions WithPrefetch(ushort count)
    {
        PrefetchCount = count;
        return this;
    }

    /// <summary>Enables or disables auto-acknowledgement of messages.</summary>
    public RabbitMqChannelOptions WithAutoAck(bool autoAck = true)
    {
        AutoAck = autoAck;
        return this;
    }

    /// <summary>
    /// Configures a durable queue (survives broker restarts, not exclusive, not auto-deleted).
    /// This is the default queue configuration.
    /// </summary>
    public RabbitMqChannelOptions WithDurableQueue()
    {
        QueueDurable = true;
        QueueExclusive = false;
        QueueAutoDelete = false;
        return this;
    }

    /// <summary>
    /// Configures a transient queue (non-durable, not exclusive, auto-deleted when empty).
    /// Suitable for temporary or test queues.
    /// </summary>
    public RabbitMqChannelOptions WithTransientQueue()
    {
        QueueDurable = false;
        QueueExclusive = false;
        QueueAutoDelete = true;
        return this;
    }

    /// <summary>Sets the queue implementation type (Classic or Quorum).</summary>
    public RabbitMqChannelOptions WithQueueType(QueueType type)
    {
        QueueType = type;
        return this;
    }

    /// <summary>Sets additional queue arguments passed to RabbitMQ on declaration.</summary>
    public RabbitMqChannelOptions WithQueueArguments(IDictionary<string, object?> arguments)
    {
        QueueArguments = arguments;
        return this;
    }

    // ── Fluent API: Retry ────────────────────────────────────────────

    /// <summary>Configures retry behavior using a builder callback.</summary>
    public RabbitMqChannelOptions WithRetry(Action<RetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Retry);
        return this;
    }

    /// <summary>Configures retry behavior with max retries and optional delay.</summary>
    public RabbitMqChannelOptions WithRetry(int maxRetries, TimeSpan? delay = null)
    {
        Retry.WithMaxRetries(maxRetries);
        if (delay.HasValue) Retry.WithDelay(delay.Value);
        return this;
    }
}
