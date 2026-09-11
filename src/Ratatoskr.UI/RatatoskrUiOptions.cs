namespace Ratatoskr.UI;

/// <summary>
/// Configuration options for the Ratatoskr web UI dashboard.
/// </summary>
public sealed class RatatoskrUiOptions
{
    /// <summary>
    /// Legacy broker-provider setting. The in-process provider ignores it.
    /// </summary>
    public string UiExchangePrefix { get; set; } = "ratatoskr-ui";

    /// <summary>
    /// Legacy broker-provider request timeout. Transport-neutral providers configure their own runtime timeout.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Time without receiving a heartbeat before a service replica is marked stale or offline.
    /// Defaults to 45 seconds.
    /// </summary>
    public TimeSpan ServiceOfflineThreshold { get; set; } = TimeSpan.FromSeconds(45);
}
