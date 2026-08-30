using System.Diagnostics.CodeAnalysis;

namespace Ratatoskr.UI;

/// <summary>
/// Configuration options for the Ratatoskr Web Management Dashboard.
/// </summary>
public sealed class RatatoskrUIOptions
{
    /// <summary>
    /// Path the Ratatoskr management API is mounted at by default. Matches the default
    /// <c>basePath</c> of <c>MapRatatoskrManagementApi</c>, so registering a remote service
    /// usually only needs its root URL.
    /// </summary>
    public const string DefaultManagementApiPath = "/ratatoskr/api/v1";

    private readonly List<RatatoskrServiceEndpoint> _remoteServices = [];

    /// <summary>
    /// Relative URL path where the dashboard is hosted. Default is "/ratatoskr".
    /// Overridden by the <c>routePrefix</c> argument of <c>MapRatatoskrUI</c> when one is passed.
    /// </summary>
    public string RoutePrefix { get; set; } = "/ratatoskr";

    /// <summary>
    /// Path the hosting service mounts its own management API at. Must match the <c>basePath</c>
    /// passed to <c>MapRatatoskrManagementApi</c>. Default is
    /// <see cref="DefaultManagementApiPath"/>.
    /// </summary>
    public string LocalManagementApiPath { get; set; } = DefaultManagementApiPath;

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
    /// Name shown in the service picker for the service hosting the dashboard itself.
    /// Default is "This Host".
    /// </summary>
    public string LocalServiceName { get; set; } = "This Host";

    /// <summary>
    /// Whether the dashboard offers the hosting service's own management API as a target.
    /// Set to false for a dedicated dashboard host that only aggregates remote services.
    /// Default is true.
    /// </summary>
    public bool IncludeLocalService { get; set; } = true;

    /// <summary>
    /// Remote Ratatoskr services registered for multi-service mode, in registration order.
    /// </summary>
    public IReadOnlyList<RatatoskrServiceEndpoint> RemoteServices => _remoteServices;

    /// <summary>
    /// Registers a remote Ratatoskr service the dashboard can inspect.
    /// </summary>
    /// <param name="name">
    /// Unique display name. Also used as the key in the dashboard proxy route, so it must not
    /// contain a slash.
    /// </param>
    /// <param name="baseUrl">
    /// Absolute root URL of the remote service, for example <c>https://orders.internal</c>.
    /// Under .NET Aspire this can be a service discovery URL such as <c>https+http://orders</c>.
    /// </param>
    /// <param name="managementApiPath">
    /// Path the remote service mounts its management API at. Defaults to
    /// <see cref="DefaultManagementApiPath"/>; override it when the remote host passed a custom
    /// <c>basePath</c> to <c>MapRatatoskrManagementApi</c>.
    /// </param>
    [SuppressMessage(
        "Design",
        "CA1054:URI-like parameters should not be strings",
        Justification = "Overload provided"
    )]
    public void AddService(
        string name,
        string baseUrl,
        string managementApiPath = DefaultManagementApiPath
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) || !IsServiceRoot(parsed))
        {
            throw new ArgumentException(
                $"Remote service '{name}' needs an absolute base URL with a host, but got '{baseUrl}'. "
                    + "Pass the service root, for example 'https://orders.internal'.",
                nameof(baseUrl)
            );
        }

        Add(name, baseUrl, managementApiPath);
    }

    /// <summary>
    /// Registers a remote Ratatoskr service the dashboard can inspect.
    /// </summary>
    public void AddService(
        string name,
        Uri baseUrl,
        string managementApiPath = DefaultManagementApiPath
    )
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        if (!IsServiceRoot(baseUrl))
        {
            throw new ArgumentException(
                $"Remote service '{name}' needs an absolute base URL with a host, but got '{baseUrl}'.",
                nameof(baseUrl)
            );
        }

        Add(name, baseUrl.ToString(), managementApiPath);
    }

    /// <summary>
    /// A usable service root is absolute and names a host. On Unix a bare path such as
    /// <c>/ratatoskr/api/v1</c> parses as an absolute <c>file:</c> URI, so checking
    /// <see cref="Uri.IsAbsoluteUri"/> alone would let a relative path through.
    /// </summary>
    private static bool IsServiceRoot(Uri uri) =>
        uri.IsAbsoluteUri && !string.IsNullOrEmpty(uri.Host);

    /// <summary>
    /// Finds a registered remote service by name (case-insensitive).
    /// </summary>
    internal RatatoskrServiceEndpoint? FindService(string name) =>
        _remoteServices.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private void Add(string name, string baseUrl, string managementApiPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The name is a single route segment in the dashboard proxy URL. A slash would split
        // into two segments and route to a different (or no) service.
        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Remote service name '{name}' must not contain '/' because it is used as a route segment.",
                nameof(name)
            );
        }

        // Names are how the dashboard addresses a service, so duplicates would make one of the
        // two unreachable and the picker ambiguous. Fail at configuration time instead.
        if (FindService(name) is { } existing)
        {
            throw new ArgumentException(
                $"A remote service named '{name}' is already registered (pointing at '{existing.ManagementApiUrl}'). "
                    + "Service names must be unique.",
                nameof(name)
            );
        }

        var root = baseUrl.TrimEnd('/');
        var path = managementApiPath?.Trim('/') ?? string.Empty;
        var managementApiUrl = path.Length == 0 ? root : $"{root}/{path}";

        _remoteServices.Add(new RatatoskrServiceEndpoint(name, managementApiUrl));
    }
}
