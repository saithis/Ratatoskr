using System.Text;
using System.Text.Json;

namespace Ratatoskr.EfCore.Management;

internal static class ManagementHelpers
{
    internal static string ExtractType(string serializedProperties)
    {
        try
        {
            using var doc = JsonDocument.Parse(serializedProperties);
            if (doc.RootElement.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "(unknown)";
        }
        catch { }
        return "(unknown)";
    }

    internal static JsonElement SafeDeserializeToJsonElement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
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
        catch
        {
            return (null, base64);
        }
    }
}
