using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Runtime;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring the Ratatoskr management agent on microservices.
/// </summary>
public static class RatatoskrManagementServiceCollectionExtensions
{
    /// <summary>
    /// Adds Ratatoskr management agent capabilities to the service.
    /// Allows the service to be monitored, queried, and managed by a transport-neutral provider.
    /// </summary>
    public static IServiceCollection AddRatatoskrManagement(
        this IServiceCollection services,
        Action<RatatoskrManagementOptions>? configure = null
    )
    {
        services.AddOptions<RatatoskrManagementOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RatatoskrManagementOptions>, RatatoskrManagementOptionsValidator>());
        services.AddOptions<ManagementRuntimeOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ManagementRuntimeOptions>, ManagementRuntimeOptionsValidator>());
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<EfCoreManagementOperations>();
        services.TryAddSingleton<ManagementOperationDispatcher>();
        services.TryAddSingleton<IManagementCommandDispatcher>(sp => sp.GetRequiredService<ManagementOperationDispatcher>());
        EfCoreManagementOperationHandlers.Add(services);
        services.TryAddSingleton<ManagementRequestHandler>();
        services.TryAddSingleton<IManagementCommandHost, InProcessManagementCommandHost>();
        services.TryAddSingleton<InProcessServiceCatalog>();
        services.TryAddSingleton<IServiceCatalog>(sp => sp.GetRequiredService<InProcessServiceCatalog>());
        services.TryAddSingleton<IManagementEventPublisher>(sp => sp.GetRequiredService<InProcessServiceCatalog>());
        services.TryAddSingleton<IManagementEventSource>(sp => sp.GetRequiredService<InProcessServiceCatalog>());
        services.TryAddSingleton<IManagementClient, InProcessManagementClient>();
        services.AddHostedService<InProcessManagementAnnouncementService>();

        return services;
    }
}
