namespace Ratatoskr.RabbitMq;

public class RabbitMqOptions
{
    /// <summary>
    /// RabbitMQ connection string (e.g., "amqp://guest:guest@localhost:5672/")
    /// </summary>
    public Uri? ConnectionString { get; set; }
    
    /// <summary>
    /// Whether to wait for publisher confirms
    /// </summary>
    public bool UsePublisherConfirms { get; set; } = true;

    /// <summary>
    /// The maximum size of an inbound message in bytes. If set, messages larger than this limit will be rejected.
    /// </summary>
    public int? MaxInboundMessageSize { get; set; }

    /// <summary>
    /// Maximum time to wait for in-flight message handlers to finish after consumer subscriptions are cancelled during shutdown.
    /// Align with <see cref="Microsoft.Extensions.Hosting.HostOptions.ShutdownTimeout"/> (default 30s) so the host does not force-kill the process while handlers are still draining.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
