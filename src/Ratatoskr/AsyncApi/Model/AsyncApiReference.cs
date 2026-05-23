using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

public class AsyncApiReference
{
    [JsonPropertyName("$ref")]
    public string Ref { get; set; }

    public AsyncApiReference(string @ref) => Ref = @ref;

    public static AsyncApiReference ToChannel(string channelName) =>
        new($"#/channels/{channelName}");

    public static AsyncApiReference ToComponentMessage(string messageName) =>
        new($"#/components/messages/{messageName}");

    public static AsyncApiReference ToChannelMessage(string channelName, string messageName) =>
        new($"#/channels/{channelName}/messages/{messageName}");

    public static AsyncApiReference ToServer(string serverName) => new($"#/servers/{serverName}");

    public static AsyncApiReference ToSchema(string schemaName) =>
        new($"#/components/schemas/{schemaName}");
}
