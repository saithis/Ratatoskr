using System.Text.Json.Serialization;

namespace Ratatoskr.AsyncApi.Model;

/// <summary>
/// Represents an AsyncAPI v3 JSON reference ($ref) pointing to another component in the document.
/// </summary>
public sealed class AsyncApiReference
{
    /// <summary>The JSON reference string (e.g. "#/channels/my-channel").</summary>
    [JsonPropertyName("$ref")]
    public string Ref { get; set; }

    /// <summary>Initializes a new reference with the given $ref value.</summary>
    public AsyncApiReference(string @ref) => Ref = @ref;

    /// <summary>Creates a reference to a channel by name.</summary>
    public static AsyncApiReference ToChannel(string channelName) =>
        new($"#/channels/{channelName}");

    /// <summary>Creates a reference to a message in the components/messages section.</summary>
    public static AsyncApiReference ToComponentMessage(string messageName) =>
        new($"#/components/messages/{messageName}");

    /// <summary>Creates a reference to a message defined on a specific channel.</summary>
    public static AsyncApiReference ToChannelMessage(string channelName, string messageName) =>
        new($"#/channels/{channelName}/messages/{messageName}");

    /// <summary>Creates a reference to a server by name.</summary>
    public static AsyncApiReference ToServer(string serverName) => new($"#/servers/{serverName}");

    /// <summary>Creates a reference to a schema in the components/schemas section.</summary>
    public static AsyncApiReference ToSchema(string schemaName) =>
        new($"#/components/schemas/{schemaName}");
}
