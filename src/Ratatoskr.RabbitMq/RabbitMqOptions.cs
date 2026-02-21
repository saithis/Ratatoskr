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
}
