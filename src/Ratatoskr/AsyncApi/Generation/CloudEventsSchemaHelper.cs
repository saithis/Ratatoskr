using Ratatoskr.AsyncApi.Model;

namespace Ratatoskr.AsyncApi.Generation;

/// <summary>
/// Builds CloudEvents-specific schemas for message payloads (structured mode).
/// </summary>
internal static class CloudEventsSchemaHelper
{
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
            Properties = new Dictionary<string, JsonSchema>(StringComparer.Ordinal)
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
                    Type = new[] { "string", "null" },
                    Format = "date-time",
                    Description = "Timestamp of when the occurrence happened.",
                },
                ["datacontenttype"] = new JsonSchema
                {
                    Type = new[] { "string", "null" },
                    Description = "Content type of the data value.",
                },
                ["subject"] = new JsonSchema
                {
                    Type = new[] { "string", "null" },
                    Description = "Describes the subject of the event.",
                },
                ["data"] = dataSchema,
            },
            Required = ["specversion", "id", "source", "type"],
        };
    }
}
