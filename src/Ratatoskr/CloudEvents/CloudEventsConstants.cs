namespace Ratatoskr.CloudEvents;

/// <summary>
/// Well-known constant values used for CloudEvents message formatting.
/// </summary>
public static class CloudEventsConstants
{
    /// <summary>The CloudEvents specification version supported by Ratatoskr ("1.0").</summary>
    public const string SpecVersion = "1.0";

    /// <summary>The content type for CloudEvents in structured JSON content mode.</summary>
    public const string JsonContentType = "application/cloudevents+json";
}
