using System.Diagnostics.CodeAnalysis;

namespace Ratatoskr.UI;

/// <summary>
/// A remote Ratatoskr service that the dashboard aggregates in multi-service mode.
/// Created through <see cref="RatatoskrUIOptions.AddService(string, string, string)"/>, which
/// validates the name and resolves the absolute management API URL.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "Serialized to the browser as JSON; a Uri would round-trip through a string anyway"
)]
public sealed record RatatoskrServiceEndpoint
{
    internal RatatoskrServiceEndpoint(string name, string managementApiUrl)
    {
        Name = name;
        ManagementApiUrl = managementApiUrl;
    }

    /// <summary>
    /// Display name of the service. Doubles as the key in the dashboard proxy route
    /// (<c>{routePrefix}/ui-api/proxy/{name}/...</c>), so it is unique and free of slashes.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Absolute base URL of the remote management API, including the path it is mounted at
    /// (for example <c>https://orders.internal/ratatoskr/api/v1</c>). The dashboard proxy
    /// appends endpoint paths such as <c>/system/metrics</c> to it verbatim.
    /// </summary>
    public string ManagementApiUrl { get; }
}
