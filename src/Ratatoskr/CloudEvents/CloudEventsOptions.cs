namespace Ratatoskr.CloudEvents;

/// <summary>
/// Options for CloudEvents protocol binding.
/// </summary>
public sealed class CloudEventsOptions
{
    /// <summary>
    /// Content mode for CloudEvents serialization. Default is Binary (more efficient).
    /// </summary>
    public CloudEventsContentMode ContentMode { get; set; } = CloudEventsContentMode.Binary;

    /// <summary>
    /// Default source URI if not specified per message or message type. Default is "/".
    /// </summary>
    public string DefaultSource { get; set; } = "/";
}
