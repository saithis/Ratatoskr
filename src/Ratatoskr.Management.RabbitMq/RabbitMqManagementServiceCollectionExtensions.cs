using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Registers RabbitMQ as the management control-plane provider.</summary>
public static class RabbitMqManagementServiceCollectionExtensions
{
    public static IServiceCollection AddRabbitMqManagement(this IServiceCollection services, Action<RabbitMqManagementOptions>? configure = null)
    {
        services.AddOptions<RabbitMqManagementOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RabbitMqManagementOptions>, RabbitMqManagementOptionsValidator>());
        if (configure is not null) services.Configure(configure);
        services.Replace(ServiceDescriptor.Singleton<RabbitMqManagementRuntime, RabbitMqManagementRuntime>());
        services.Replace(ServiceDescriptor.Singleton<IManagementClient>(sp => sp.GetRequiredService<RabbitMqManagementRuntime>()));
        services.Replace(ServiceDescriptor.Singleton<IServiceCatalog>(sp => sp.GetRequiredService<RabbitMqManagementRuntime>()));
        services.Replace(ServiceDescriptor.Singleton<IManagementEventSource>(sp => sp.GetRequiredService<RabbitMqManagementRuntime>()));
        services.Replace(ServiceDescriptor.Singleton<IManagementEventPublisher>(sp => sp.GetRequiredService<RabbitMqManagementRuntime>()));
        services.AddHostedService(sp => sp.GetRequiredService<RabbitMqManagementRuntime>());
        // A dashboard needs only discovery and a client. Registering a command consumer there
        // would require an application dispatcher that it deliberately does not have.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IManagementCommandDispatcher)))
        {
            services.TryAddSingleton<IManagementCommandHost, RabbitMqManagementCommandHost>();
            services.AddHostedService<RabbitMqManagementCommandConsumer>();
        }
        return services;
    }
}
