namespace Ratatoskr.UI;

/// <summary>
/// Configuration options for the Ratatoskr web UI dashboard.
/// </summary>
public sealed class RatatoskrUiOptions
{
    /// <summary>
    /// Name prefix for the UI management exchanges ({UiExchangePrefix}.commands and {UiExchangePrefix}.inbox).
    /// Must match the UiExchangePrefix configured on managed services.
    /// Defaults to "ratatoskr-ui".
    /// </summary>
    public string UiExchangePrefix { get; set; } = "ratatoskr-ui";

    /// <summary>
    /// Timeout for RPC management requests to remote services.
    /// Defaults to 10 seconds.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Time without receiving a heartbeat before a service replica is marked stale or offline.
    /// Defaults to 45 seconds.
    /// </summary>
    public TimeSpan ServiceOfflineThreshold { get; set; } = TimeSpan.FromSeconds(45);
}
