using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Management;

internal static class ManagementHelpers
{
    /// <summary>
    /// Builds a SQL LIKE pattern for a general substring search against a serialized
    /// <see cref="Core.MessageProperties"/> blob (or any JSON string).
    /// LIKE metacharacters (<c>%</c>, <c>_</c>, <c>\</c>) in the user-supplied value are
    /// escaped with <c>\</c>, so callers must pass <c>@"\"</c> as the escape character to
    /// <c>EF.Functions.Like</c>.
    /// </summary>
    internal static string BuildSearchPattern(string search) =>
        "%" + search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    internal static string ExtractType(string serializedProperties, ILogger? logger = null)
    {
        try
        {
            var properties = JsonSerializer.Deserialize<MessageProperties>(serializedProperties);
            return properties?.Type ?? "(unknown)";
        }
        catch (JsonException ex)
        {
            // Corrupt serialized properties — surface to the operator while still
            // returning a sentinel so the UI can render the row.
            logger?.LogWarning(ex, "Failed to parse serialized message properties; returning '(unknown)' type.");
        }
        return "(unknown)";
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
