using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static partial class ManagementHelpers
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
            var properties = BaseMessageEntity.DeserializeMessageProperties(serializedProperties);
            return properties.Type ?? "(unknown)";
        }
        catch (MessagePropertiesDeserializationException ex)
        {
            // Corrupt serialized properties — surface to the operator while still
            // returning a sentinel so the UI can render the row.
            if (logger != null)
            {
                LogFailedToParseMessageProperties(logger, ex);
            }
        }
        return "(unknown)";
    }

    internal static (string? JsonPayload, string PayloadBase64) DecodeContent(
        byte[] content,
        ILogger? logger = null
    )
    {
        var base64 = Convert.ToBase64String(content);
        try
        {
            var text = Encoding.UTF8.GetString(content);
            using var doc = JsonDocument.Parse(text);
            return (text, base64);
        }
        catch (Exception ex)
        {
            if (logger != null)
            {
                LogPayloadNotJson(logger, ex);
            }
            return (null, base64);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Failed to parse serialized message properties; returning '(unknown)' type."
    )]
    private static partial void LogFailedToParseMessageProperties(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Message payload is not JSON; only base64 will be surfaced."
    )]
    private static partial void LogPayloadNotJson(ILogger logger, Exception ex);
}
