namespace Ratatoskr.Core;

public sealed class MessageProperties
{
    /// <summary>
    /// Unique identifier for this event. Auto-generated if not set.
    /// Maps to CloudEvents "id" field.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// The type of the message, e.g. "com.example.order-shipped".
    /// Maps to CloudEvents "type" field.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// URI identifying the event source, e.g. "/orders-service".
    /// Maps to CloudEvents "source" field.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Subject of the event, e.g. order ID.
    /// Maps to CloudEvents "subject" field.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// URI identifying the schema that the event data adheres to.
    /// Maps to CloudEvents "dataschema" attribute (optional).
    /// </summary>
    public string? DataSchema { get; set; }

    /// <summary>
    /// Content type of the data payload.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Event timestamp. Auto-set to current time if not specified.
    /// </summary>
    public DateTimeOffset? Time { get; set; }

    /// <summary>
    /// W3C Traceparent header - Contains a version, trace ID, span ID, and trace options
    /// https://w3c.github.io/trace-context/#traceparent-header
    /// </summary>
    public string? TraceParent { get; set; }

    /// <summary>
    /// W3C Tracestate header - a comma-delimited list of key-value pairs
    /// https://w3c.github.io/trace-context/#tracestate-header
    /// </summary>
    public string? TraceState { get; set; }

    /// <summary>
    /// Custom headers to include with the message.
    /// </summary>
    public IDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The transports this message should be sent over.
    /// </summary>
    public ISet<string> Transports { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Transport-specific metadata (e.g., RabbitMQ exchange/routing key).
    /// Not included in CloudEvents envelope.
    /// </summary>
    public IDictionary<string, string> TransportMetadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// CloudEvents extension attributes (included in envelope).
    /// </summary>
    public IDictionary<string, object> CloudEventExtensions { get; init; } =
        new Dictionary<string, object>(StringComparer.Ordinal);
}
