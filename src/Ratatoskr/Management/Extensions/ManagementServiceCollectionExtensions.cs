using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Http;

namespace Ratatoskr.Management;

/// <summary>Registers the transport-neutral parts of the management agent.</summary>
public static class ManagementServiceCollectionExtensions
{
    /// <summary>
    /// Adds the management dispatcher, the agent that describes this process, and the shared HTTP
    /// plumbing. Storage packages add their operations on top; transport packages add their own
    /// registration call.
    /// </summary>
    public static IServiceCollection AddRatatoskrManagementCore(
        this IServiceCollection services,
        Action<ManagementAgentOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ManagementAgentOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<ManagementAgentOptions>,
                ManagementAgentOptionsValidator
            >()
        );

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ManagementAgent>();
        services.TryAddSingleton<IManagementDispatcher, ManagementDispatcher>();

        services.TryAddSingleton<ManagementAntiforgeryOptions>();
        services.AddAntiforgery();
        services.TryAddSingleton<ManagementAntiforgeryFilter>();
        services.TryAddSingleton<LocalManagementDispatchStrategy>();

        // service.describe is transport-neutral: it answers from the agent, which composes
        // whatever capability, topology and summary contributors happen to be registered.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IManagementOperation, DescribeServiceOperation>()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IManagementTopologyContributor, ChannelRegistryTopologyContributor>()
        );

        return services;
    }

    /// <summary>
    /// Registers one management operation. Operations are scoped because they reach into a scoped
    /// <c>DbContext</c>; the dispatcher creates a scope per execution so the singleton command
    /// consumers that call it cannot capture one.
    /// </summary>
    public static IServiceCollection AddManagementOperation<TOperation>(
        this IServiceCollection services
    )
        where TOperation : class, IManagementOperation
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IManagementOperation, TOperation>());
        return services;
    }
}
