using System.Diagnostics.CodeAnalysis;

namespace Ratatoskr.UI;

/// <summary>
/// Configuration options for the Ratatoskr Web Management Dashboard.
/// </summary>
public sealed class RatatoskrUIOptions
{
    private readonly List<RatatoskrServiceEndpoint> _remoteServices = [];

    /// <summary>
    /// Relative URL path where the dashboard will be hosted. Default is "/ratatoskr".
    /// </summary>
    public string RoutePrefix { get; set; } = "/ratatoskr";

    /// <summary>
    /// Page title displayed in the dashboard header. Default is "Ratatoskr Dashboard".
    /// </summary>
    public string Title { get; set; } = "Ratatoskr Dashboard";

    /// <summary>
    /// Auto-refresh interval in milliseconds for dashboard metrics and workbench. Default is 5000ms.
    /// </summary>
    public int PollingIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Whether payload editing is allowed before requeueing poison messages. Default is true.
    /// </summary>
    public bool EnablePayloadEditing { get; set; } = true;

    /// <summary>
    /// Registered remote Ratatoskr services for Multi-Service Hub mode.
    /// </summary>
    public IReadOnlyList<RatatoskrServiceEndpoint> RemoteServices => _remoteServices;

    /// <summary>
    /// Registers a remote service management API URL for central aggregation.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1054:URI-like parameters should not be strings",
        Justification = "Overload provided"
    )]
    public void AddService(string name, string managementApiUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(managementApiUrl);

        _remoteServices.Add(new RatatoskrServiceEndpoint(name, managementApiUrl));
    }

    /// <summary>
    /// Registers a remote service management API URL for central aggregation.
    /// </summary>
    public void AddService(string name, Uri managementApiUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(managementApiUrl);

        AddService(name, managementApiUrl.ToString());
    }
}
