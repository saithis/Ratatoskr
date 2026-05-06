namespace Ratatoskr.UI;

/// <summary>
/// Configuration for the Ratatoskr Management UI proxy.
/// </summary>
public sealed class RatatoskrUiOptions
{
    /// <summary>
    /// Base URL path for the UI and its API. Defaults to <c>/ratatoskr</c>.
    /// </summary>
    public string BasePath { get; set; } = "/ratatoskr";

    /// <summary>
    /// Authorization policy name applied to all UI proxy routes.
    /// The policy must be registered before calling <c>MapRatatoskrUiRoutes</c>.
    /// </summary>
    public string PolicyName { get; set; } = string.Empty;

    internal List<BackendRegistration> Backends { get; } = [];

    /// <summary>
    /// Adds a local (in-process) backend. Requests are dispatched through the ASP.NET Core
    /// pipeline without an HTTP round-trip. The caller's <see cref="System.Security.Claims.ClaimsPrincipal"/>
    /// is propagated to the synthetic context so the management policy still applies.
    /// </summary>
    public void AddLocalBackend(string name)
        => Backends.Add(new BackendRegistration(name, null, null, IsLocal: true));

    /// <summary>
    /// Adds a remote backend reachable via HTTP. Optionally configure auth via
    /// <paramref name="configureAuth"/> to attach tokens or headers to outbound requests.
    /// </summary>
    public void AddBackend(string name, string baseUrl,
        Action<AuthDelegateBuilder>? configureAuth = null)
    {
        var authBuilder = new AuthDelegateBuilder();
        configureAuth?.Invoke(authBuilder);
        Backends.Add(new BackendRegistration(name, baseUrl, authBuilder.Build(), IsLocal: false));
    }
}

internal sealed record BackendRegistration(
    string Name,
    string? BaseUrl,
    Func<HttpRequestMessage, Task>? AuthDelegate,
    bool IsLocal);
