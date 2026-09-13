using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.EfCore.Internal;

/// <summary>
/// Turns stored message rows into the shapes the protocol defines. Kept in one place because the
/// failure modes — corrupt properties, a non-JSON body — have to be handled identically by the
/// list, detail and search paths or the UI shows three different kinds of nothing.
/// </summary>
internal static partial class ManagementPayloadDecoder
{
    /// <summary>
    /// Builds a SQL <c>LIKE</c> pattern for a substring search over serialized properties.
    /// LIKE metacharacters in the user's input are escaped with <c>\</c>, so callers must pass
    /// <c>@"\"</c> as the escape character to <c>EF.Functions.Like</c>.
    /// </summary>
    internal static string BuildSearchPattern(string search) =>
        "%"
        + search
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
        + "%";

    /// <summary>
    /// Reads the CloudEvents type out of a serialized properties blob, falling back to a sentinel
    /// so one corrupt row cannot blank out a whole page of the list.
    /// </summary>
    internal static string ExtractType(string serializedProperties, ILogger logger)
    {
        try
        {
            return BaseMessageEntity.DeserializeMessageProperties(serializedProperties).Type
                ?? UnknownType;
        }
        catch (MessagePropertiesDeserializationException ex)
        {
            LogUnreadableProperties(logger, ex);
            return UnknownType;
        }
    }

    /// <summary>Projects CloudEvents metadata onto the wire shape.</summary>
    internal static MessagePropertiesView ToView(MessageProperties properties) =>
        new(
            properties.Id,
            properties.Type,
            properties.Source,
            properties.Subject,
            properties.DataSchema,
            properties.ContentType,
            properties.Time,
            properties.ScheduledAt,
            properties.TraceParent
        );

    /// <summary>
    /// Decodes a stored body. Returns the text only when it really is JSON, plus base64 always,
    /// so a caller can render a payload it understands and still download one it does not.
    /// </summary>
    internal static (string? JsonPayload, string PayloadBase64) DecodeContent(
        byte[] content,
        ILogger logger
    )
    {
        var base64 = Convert.ToBase64String(content);
        try
        {
            var text = Encoding.UTF8.GetString(content);
            using var document = JsonDocument.Parse(text);
            return (text, base64);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            LogPayloadNotJson(logger, ex);
            return (null, base64);
        }
    }

    internal const string UnknownType = "(unknown)";

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Serialized message properties could not be read; reporting the type as '(unknown)'."
    )]
    private static partial void LogUnreadableProperties(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Message payload is not JSON; only base64 will be surfaced."
    )]
    private static partial void LogPayloadNotJson(ILogger logger, Exception exception);
}
