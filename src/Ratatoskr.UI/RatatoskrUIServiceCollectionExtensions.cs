using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.UI;

/// <summary>
/// Extension methods for registering Ratatoskr UI services.
/// </summary>
public static class RatatoskrUIServiceCollectionExtensions
{
    /// <summary>
    /// Adds Ratatoskr Management Dashboard UI services and options.
    /// </summary>
    public static IServiceCollection AddRatatoskrUI(
        this IServiceCollection services,
        Action<RatatoskrUIOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RatatoskrUIOptions();
        configure?.Invoke(options);

        _ = services.AddSingleton(options);
        _ = services.AddHttpClient("RatatoskrUIProxy");

        return services;
    }
}
