using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ratatoskr.Management.Contracts;

/// <summary>
/// The one serializer configuration the control plane uses. Every producer and consumer of a
/// management envelope — both HTTP surfaces, every transport, the dashboard store — shares it, so
/// a value cannot mean one thing on the wire and another in the database.
/// </summary>
public static class ManagementJson
{
    /// <summary>The canonical options.</summary>
    /// <remarks>
    /// Enums are written as names rather than ordinals: a protocol whose meaning shifts when
    /// someone inserts an enum member in the middle is not a protocol. Nulls are omitted because
    /// envelopes are mostly optional fields and every byte crosses a broker.
    /// </remarks>
    public static JsonSerializerOptions Options { get; } =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

    /// <summary>Serializes <paramref name="value"/> to a <see cref="JsonElement"/>.</summary>
    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);

    /// <summary>Serializes <paramref name="value"/> to a <see cref="JsonElement"/>.</summary>
    public static JsonElement ToElement(object? value, Type type) =>
        JsonSerializer.SerializeToElement(value, type, Options);

    /// <summary>Deserializes a payload element, treating an absent payload as absent.</summary>
    public static T? FromElement<T>(JsonElement? element) =>
        element is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            ? default
            : element.Value.Deserialize<T>(Options);

    /// <summary>Deserializes a payload element to <paramref name="type"/>.</summary>
    public static object? FromElement(JsonElement? element, Type type) =>
        element is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
            ? null
            : element.Value.Deserialize(type, Options);
}
