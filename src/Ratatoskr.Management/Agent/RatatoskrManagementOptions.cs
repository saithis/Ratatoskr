using System.Reflection;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Configuration options for the Ratatoskr management agent.
/// </summary>
public sealed class RatatoskrManagementOptions
{
    private string? _serviceName;

    /// <summary>
    /// Logical name of the service (e.g. "orders", "inventory").
    /// Defaults to the entry assembly name.
    /// </summary>
    public string ServiceName
    {
        get => _serviceName ??= Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant() ?? "service";
        set => _serviceName = value;
    }

    /// <summary>
    /// Unique replica instance ID (e.g. pod name, container ID, or short guid).
    /// </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Host machine name. Defaults to Environment.MachineName.
    /// </summary>
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Name prefix for the UI management exchanges ({UiExchangePrefix}.commands and {UiExchangePrefix}.inbox).
    /// Defaults to "ratatoskr-ui".
    /// </summary>
    public string UiExchangePrefix { get; set; } = "ratatoskr-ui";

    /// <summary>
    /// Interval between heartbeat reports sent to the UI.
    /// Defaults to 15 seconds.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether to send periodic heartbeats to the UI.
    /// Defaults to true.
    /// </summary>
    public bool EnableHeartbeat { get; set; } = true;
}
