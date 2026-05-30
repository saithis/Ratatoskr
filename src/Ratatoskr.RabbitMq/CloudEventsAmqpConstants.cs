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
    public const string IdHeader = "id";
    public const string SourceHeader = "source";
    public const string TypeHeader = "type";
    public const string TimeHeader = "time";
    public const string SubjectHeader = "subject";
    public const string DataContentTypeHeader = "datacontenttype";
    public const string DataSchemaHeader = "dataschema";

    /// <summary>
    /// Trace propagation header (W3C Trace Context).
    /// </summary>
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";
}
