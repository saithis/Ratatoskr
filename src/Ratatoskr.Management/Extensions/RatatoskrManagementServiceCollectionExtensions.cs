using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.Management.Agent;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring the Ratatoskr management agent on microservices.
/// </summary>
public static class RatatoskrManagementServiceCollectionExtensions
{
    /// <summary>
    /// Adds Ratatoskr management agent capabilities to the service.
    /// Allows the service to be monitored, queried, and managed by Ratatoskr.UI over RabbitMQ or in-process.
    /// </summary>
    public static IServiceCollection AddRatatoskrManagement(
        this IServiceCollection services,
        Action<RatatoskrManagementOptions>? configure = null
    )
    {
        services.AddOptions<RatatoskrManagementOptions>();
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ManagementRequestHandler>();

        services.TryAddSingleton<RabbitMqManagementAgentConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<RabbitMqManagementAgentConsumer>());

        services.TryAddSingleton<ServiceHeartbeatReporter>();
        services.AddHostedService(sp => sp.GetRequiredService<ServiceHeartbeatReporter>());

        return services;
    }
}
