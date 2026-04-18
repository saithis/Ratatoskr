using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.UI.Proxy;

namespace Ratatoskr.UI;

/// <summary>
/// Extension methods for registering Ratatoskr UI services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers services required by the Ratatoskr Management UI proxy.
    /// Call this from <c>builder.Services</c> before building the app.
    /// </summary>
    /// <remarks>
    /// This also registers <see cref="IHttpClientFactory"/> (via <c>AddHttpClient</c>),
    /// which is used to forward requests to remote backends.
    /// </remarks>
    public static IServiceCollection AddRatatoskrUi(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.AddSingleton<LocalPipelineHolder>();
        // TryAdd so tests can register a pre-configured holder before calling AddRatatoskrUi.
        services.TryAddSingleton<RatatoskrUiOptionsHolder>();
        services.AddScoped<LocalBackendDispatcher>();
        return services;
    }

    /// <summary>
    /// Registers Ratatoskr UI services and pre-configures the UI options so that
    /// <c>UseRatatoskrUiIfConfigured</c> / <c>MapRatatoskrUiRoutesIfConfigured</c> activate
    /// automatically in the shared test host.
    /// </summary>
    /// <remarks>
    /// Use this overload in tests that verify the UI proxy without calling
    /// <c>UseRatatoskrUi</c> directly (e.g., when the pipeline is managed by
    /// <c>Ratatoskr.TestHost</c>).
    /// </remarks>
    public static IServiceCollection AddRatatoskrUi(
        this IServiceCollection services,
        Action<RatatoskrUiOptions> configure)
    {
        var options = new RatatoskrUiOptions();
        configure(options);

        var holder = new RatatoskrUiOptionsHolder { Options = options };
        services.AddSingleton(holder);

        return AddRatatoskrUi(services);
    }
}
