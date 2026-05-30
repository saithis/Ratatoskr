namespace Ratatoskr.RabbitMq;

/// <summary>
/// Constants for CloudEvents AMQP protocol binding.
/// See: https://github.com/cloudevents/spec/blob/main/cloudevents/bindings/amqp-protocol-binding.md
/// </summary>
public static class CloudEventsAmqpConstants
{
    /// <summary>
    /// CloudEvents specification version.
    /// </summary>
    public const string SpecVersion = "1.0";

    /// <summary>
    /// Content type for structured mode.
    /// </summary>
    public const string JsonContentType = "application/cloudevents+json";

    /// <summary>
    /// Header prefix for CloudEvents attributes in binary content mode.
    /// Uses underscore separator (JMS 2.0 compatible, preferred by Wolverine).
    /// </summary>
    public const string HeaderPrefix = "cloudEvents_";

    /// <summary>
    /// Alternative header prefix using colon separator.
    /// Supported for incoming messages (per AMQP binding spec).
    /// </summary>
    public const string AlternativeHeaderPrefix = "cloudEvents:";

    /// <summary>CloudEvents attribute header names (attribute name portion, used with <see cref="HeaderPrefix"/>).</summary>
    public const string SpecVersionHeader = "specversion";

    /// <summary>Attribute name for the unique event identifier.</summary>
    public const string IdHeader = "id";

    /// <summary>Attribute name for the event source URI.</summary>
    public const string SourceHeader = "source";

    /// <summary>Attribute name for the CloudEvents event type.</summary>
    public const string TypeHeader = "type";

    /// <summary>Attribute name for the event occurrence timestamp.</summary>
    public const string TimeHeader = "time";

    /// <summary>Attribute name describing the subject of the event.</summary>
    public const string SubjectHeader = "subject";

    /// <summary>Attribute name for the content type of the event data.</summary>
    public const string DataContentTypeHeader = "datacontenttype";

    /// <summary>Attribute name for the URI identifying the schema of the event data.</summary>
    public const string DataSchemaHeader = "dataschema";

    /// <summary>
    /// Trace propagation header (W3C Trace Context).
    /// </summary>
    public const string TraceParentHeader = "traceparent";

    /// <summary>W3C Trace Context tracestate header for vendor-specific trace metadata.</summary>
    public const string TraceStateHeader = "tracestate";
}
