using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Represents a JSON Schema object used for message payload and header schemas.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "DTO for JSON serialization"
)]
[SuppressMessage(
    "Usage",
    "CA2227:CollectionPropertiesShouldBeReadOnly",
    Justification = "DTO for JSON serialization"
)]
public sealed class JsonSchema
{
    /// <summary>JSON Schema $ref to another schema definition.</summary>
    [JsonPropertyName("$ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; set; }

    /// <summary>
    /// JSON Schema type. A single type string (e.g. "string") or an array of types
    /// (e.g. ["string", "null"]) for nullable values per JSON Schema Draft-07.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Type { get; set; }

    /// <summary>JSON Schema format hint (e.g. "date-time", "uuid").</summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    /// <summary>Human-readable description of the schema or property.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Combines multiple schemas (used for nullable $ref types in JSON Schema Draft-07).</summary>
    [JsonPropertyName("oneOf")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonSchema>? OneOf { get; set; }

    /// <summary>List of property names that are required in the object.</summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Required { get; set; }

    /// <summary>Named property schemas for an object type.</summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonSchema>? Properties { get; set; }

    /// <summary>Schema for additional (unspecified) properties of an object.</summary>
    [JsonPropertyName("additionalProperties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchema? AdditionalProperties { get; set; }

    /// <summary>Schema for items in an array type.</summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchema? Items { get; set; }

    /// <summary>The set of allowed values for this property.</summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<object>? Enum { get; set; }

    /// <summary>Human-readable names for enum values (OpenAPI extension).</summary>
    [JsonPropertyName("x-enumNames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? XEnumNames { get; set; }

    /// <summary>Variable names for enum values used by code generators (OpenAPI extension).</summary>
    [JsonPropertyName("x-enum-varnames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? XEnumVarnames { get; set; }

    /// <summary>Maximum allowed string length.</summary>
    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    /// <summary>Minimum allowed string length.</summary>
    [JsonPropertyName("minLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinLength { get; set; }

    /// <summary>Minimum allowed numeric value (inclusive).</summary>
    [JsonPropertyName("minimum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Minimum { get; set; }

    /// <summary>Maximum allowed numeric value (inclusive).</summary>
    [JsonPropertyName("maximum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Maximum { get; set; }

    /// <summary>Regular expression pattern that the string value must match.</summary>
    [JsonPropertyName("pattern")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pattern { get; set; }

    /// <summary>Creates a $ref schema pointing to components/schemas/{name}.</summary>
    public static JsonSchema RefTo(string schemaName) =>
        new() { Ref = $"#/components/schemas/{schemaName}" };
}
