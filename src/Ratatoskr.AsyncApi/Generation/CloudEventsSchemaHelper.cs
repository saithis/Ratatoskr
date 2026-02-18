using Ratatoskr.AsyncApi.Model;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.AsyncApi.Generation;

/// <summary>
/// Builds CloudEvents-specific schemas for message headers (binary mode) and payloads (structured mode).
/// </summary>
internal static class CloudEventsSchemaHelper
{
    /// <summary>
    /// Returns the AMQP application-properties schema documenting the CloudEvents attributes
    /// sent in binary content mode. Uses the <c>cloudEvents_</c> prefix as per the implementation
    /// in <see cref="CloudEventsAmqpConstants"/>.
    /// </summary>
    public static JsonSchema BuildBinaryModeHeadersSchema()
    {
        var prefix = CloudEventsAmqpConstants.HeaderPrefix; // "cloudEvents_"

        return new JsonSchema
        {
            Type = "object",
            Description = "AMQP application-properties carrying CloudEvents attributes (binary content mode).",
            Properties = new Dictionary<string, JsonSchema>
            {
                [$"{prefix}specversion"] = new JsonSchema
                {
                    Type = "string",
                    Description = "CloudEvents specification version.",
                    Enum = ["1.0"],
                },
                [$"{prefix}id"] = new JsonSchema
                {
                    Type = "string",
                    Description = "Unique identifier for the event.",
                },
                [$"{prefix}type"] = new JsonSchema
                {
                    Type = "string",
                    Description = "CloudEvent type identifier (e.g. com.example.order.created).",
                },
                [$"{prefix}source"] = new JsonSchema
                {
                    Type = "string",
                    Format = "uri-reference",
                    Description = "Identifies the context in which an event happened.",
                },
                [$"{prefix}time"] = new JsonSchema
                {
                    Type = "string",
                    Format = "date-time",
                    Description = "Timestamp of when the occurrence happened.",
                    Nullable = true,
                },
                [$"{prefix}datacontenttype"] = new JsonSchema
                {
                    Type = "string",
                    Description = "Content type of the data value (e.g. application/json).",
                    Nullable = true,
                },
                [$"{prefix}subject"] = new JsonSchema
                {
                    Type = "string",
                    Description = "Describes the subject of the event in the context of the event producer.",
                    Nullable = true,
                },
                ["traceparent"] = new JsonSchema
                {
                    Type = "string",
                    Description = "W3C Trace Context traceparent header for distributed tracing.",
                    Nullable = true,
                },
                ["tracestate"] = new JsonSchema
                {
                    Type = "string",
                    Description = "W3C Trace Context tracestate header.",
                    Nullable = true,
                },
            },
            Required = [$"{prefix}specversion", $"{prefix}id", $"{prefix}type", $"{prefix}source"],
        };
    }

    /// <summary>
    /// Returns a schema for the CloudEvent envelope used in structured content mode.
    /// The <paramref name="dataSchema"/> is inlined as the <c>data</c> property.
    /// </summary>
    public static JsonSchema BuildStructuredModePayloadSchema(JsonSchema dataSchema)
    {
        return new JsonSchema
        {
            Type = "object",
            Description = "CloudEvent envelope (structured content mode).",
            Properties = new Dictionary<string, JsonSchema>
            {
                ["specversion"] = new JsonSchema
                {
                    Type = "string",
                    Description = "CloudEvents specification version.",
                    Enum = ["1.0"],
                },
                ["id"] = new JsonSchema
                {
                    Type = "string",
                    Description = "Unique identifier for the event.",
                },
                ["source"] = new JsonSchema
                {
                    Type = "string",
                    Format = "uri-reference",
                    Description = "Identifies the context in which an event happened.",
                },
                ["type"] = new JsonSchema
                {
                    Type = "string",
                    Description = "CloudEvent type identifier.",
                },
                ["time"] = new JsonSchema
                {
                    Type = "string",
                    Format = "date-time",
                    Description = "Timestamp of when the occurrence happened.",
                    Nullable = true,
                },
                ["datacontenttype"] = new JsonSchema
                {
                    Type = "string",
                    Description = "Content type of the data value.",
                    Nullable = true,
                },
                ["subject"] = new JsonSchema
                {
                    Type = "string",
                    Description = "Describes the subject of the event.",
                    Nullable = true,
                },
                ["data"] = dataSchema,
            },
            Required = ["specversion", "id", "source", "type"],
        };
    }
}
