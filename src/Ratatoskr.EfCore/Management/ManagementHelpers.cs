using System.Text;
using System.Text.Json;

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

    internal static string ExtractType(string serializedProperties)
    {
        try
        {
            using var doc = JsonDocument.Parse(serializedProperties);
            if (doc.RootElement.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "(unknown)";
        }
        catch (JsonException)
        {
            // Corrupt serialized properties — return a sentinel so the UI can surface it.
        }
        return "(unknown)";
    }

    internal static JsonElement SafeDeserializeToJsonElement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    internal static (string? JsonPayload, string PayloadBase64) DecodeContent(byte[] content)
    {
        var base64 = Convert.ToBase64String(content);
        try
        {
            var text = Encoding.UTF8.GetString(content);
            using var doc = JsonDocument.Parse(text);
            return (text, base64);
        }
        catch (JsonException)
        {
            return (null, base64);
        }
        catch (DecoderFallbackException)
        {
            return (null, base64);
        }
    }
}
