using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.EfCore.Management;

internal static class ManagementHelpers
{
    /// <summary>
    /// Builds a SQL LIKE pattern that matches the JSON fragment
    /// <c>"Type":"&lt;value&gt;"</c> inside a serialized <see cref="Core.MessageProperties"/> blob.
    /// <para>
    /// The value is JSON-encoded (matching how the properties are stored) and LIKE
    /// metacharacters (<c>%</c>, <c>_</c>, <c>\</c>) are escaped so that user input
    /// cannot inject wildcards. The pattern is anchored with the surrounding
    /// <c>"Type":"..."</c> fragment so it cannot accidentally match characters
    /// from other fields.
    /// </para>
    /// <para>
    /// Callers must pair this pattern with an in-memory exact-match check on
    /// <see cref="ExtractType"/> — LIKE-escape semantics vary between providers
    /// (PostgreSQL defaults to <c>\</c>, SQL Server has no default escape) so this
    /// DB-side filter is only guaranteed to be a <em>superset</em> of matches.
    /// </para>
    /// </summary>
    internal static string BuildMessageTypeLikePattern(string type)
    {
        // JSON-encode the user input so it matches the stored JSON value form exactly.
        var jsonEncoded = JsonEncodedText.Encode(type).Value;

        var builder = new StringBuilder(jsonEncoded.Length + 16);
        builder.Append(@"%""Type"":""");
        foreach (var ch in jsonEncoded)
        {
            if (ch is '\\' or '%' or '_')
                builder.Append('\\');
            builder.Append(ch);
        }
        builder.Append(@"""%");
        return builder.ToString();
    }

    internal static string ExtractType(string serializedProperties, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(serializedProperties);
            if (doc.RootElement.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "(unknown)";
        }
        catch (JsonException ex)
        {
            // Corrupt serialized properties — surface to the operator while still
            // returning a sentinel so the UI can render the row.
            logger?.LogWarning(ex, "Failed to parse serialized message properties; returning '(unknown)' type.");
        }
        return "(unknown)";
    }

    internal static JsonElement SafeDeserializeToJsonElement(string json, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to parse JSON for management detail view; returning empty object.");
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    internal static (string? JsonPayload, string PayloadBase64) DecodeContent(byte[] content, ILogger? logger = null)
    {
        var base64 = Convert.ToBase64String(content);
        try
        {
            var text = Encoding.UTF8.GetString(content);
            using var doc = JsonDocument.Parse(text);
            return (text, base64);
        }
        catch (JsonException ex)
        {
            // Binary or non-JSON payload — base64 is still returned so the UI can offer a download.
            logger?.LogDebug(ex, "Message payload is not valid JSON; only base64 will be surfaced.");
            return (null, base64);
        }
        catch (DecoderFallbackException ex)
        {
            logger?.LogDebug(ex, "Message payload is not valid UTF-8; only base64 will be surfaced.");
            return (null, base64);
        }
    }
}
