namespace Ratatoskr.UI;

/// <summary>
/// Configuration options for the Ratatoskr web UI dashboard.
/// </summary>
public sealed class RatatoskrUiOptions
{
    /// <summary>
    /// Time without receiving a heartbeat before a service replica is marked stale or offline.
    /// Defaults to 45 seconds.
    /// </summary>
    public TimeSpan ServiceOfflineThreshold { get; set; } = TimeSpan.FromSeconds(45);
}

/// <summary>Authorization policies required by the distinct UI capabilities.</summary>
public sealed record RatatoskrUiAuthorizationPolicies(
    string ViewMetadata,
    string ViewPayloads,
    string RequeueMessages,
    string DeleteMessages,
    string BulkOperations);
