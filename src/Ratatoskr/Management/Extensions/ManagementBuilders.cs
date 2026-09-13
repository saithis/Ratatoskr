using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Http;
using Ratatoskr.Management.Registry;
using Ratatoskr.Management.Transports;

namespace Ratatoskr.Management;

/// <summary>
/// Configures a managed service: what it calls itself, and which control planes it announces on.
/// </summary>
/// <remarks>
/// Several transports are allowed at once, which is the migration story: a service announces on
/// the old broker and the new one simultaneously until the old dashboard is switched off.
/// </remarks>
public sealed class ManagementAgentBuilder
{
    private readonly List<Action<ManagementAgentOptions>> _configurations = [];

    internal ManagementAgentBuilder(IServiceCollection services)
    {
        Services = services;
        TransportNames = services.GetOrAddInstance<ManagementTransportNameReservations>();
    }

    /// <summary>The service collection, for transports and storage packages to add to.</summary>
    public IServiceCollection Services { get; }

    internal ManagementTransportNameReservations TransportNames { get; }

    /// <summary>The logical service name announced to the control plane.</summary>
    public string? ServiceName { get; set; }

    /// <summary>This replica's identity, unique within the service.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Adjusts any agent option.</summary>
    public ManagementAgentBuilder Configure(Action<ManagementAgentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Serves management requests from a dashboard hosted in this same process.
    /// </summary>
    public ManagementAgentBuilder AddInProcess(
        string name = ManagementTransportKinds.InProcess
    )
    {
        if (
            TransportNames.ReserveRole(
                name,
                ManagementTransportKinds.InProcess,
                ManagementTransportNameReservations.AgentRole
            )
        )
        {
            Services.AddInProcessManagementTransport(name);
        }

        return this;
    }

    internal void Apply()
    {
        Services.Configure<ManagementAgentOptions>(options =>
        {
            if (ServiceName is not null)
            {
                options.ServiceName = ServiceName;
            }

            if (InstanceId is not null)
            {
                options.InstanceId = InstanceId;
            }

            foreach (var configure in _configurations)
            {
                configure(options);
            }
        });
    }
}

/// <summary>
/// Configures a dashboard: which control planes it watches, and where it keeps its own state.
/// </summary>
public sealed class ManagementDashboardBuilder
{
    internal ManagementDashboardBuilder(IServiceCollection services)
    {
        Services = services;
        TransportNames = services.GetOrAddInstance<ManagementTransportNameReservations>();
    }

    /// <summary>The service collection, for transports and the store to add to.</summary>
    public IServiceCollection Services { get; }

    internal ManagementTransportNameReservations TransportNames { get; }

    private readonly List<Action<ManagementDashboardOptions>> _configurations = [];

    /// <summary>Adjusts any dashboard option.</summary>
    public ManagementDashboardBuilder Configure(Action<ManagementDashboardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Watches services co-hosted in this process. Requires an agent in the same process.
    /// </summary>
    public ManagementDashboardBuilder AddInProcess(
        string name = ManagementTransportKinds.InProcess
    )
    {
        if (
            TransportNames.ReserveRole(
                name,
                ManagementTransportKinds.InProcess,
                ManagementTransportNameReservations.DashboardRole
            )
        )
        {
            Services.AddInProcessManagementTransport(name);
        }

        return this;
    }

    internal void Apply()
    {
        Services.AddOptions<ManagementDashboardOptions>();
        foreach (var configure in _configurations)
        {
            Services.Configure(configure);
        }

        Services.AddManagementTransportRuntime();
    }
}

/// <summary>Registration entry points for the management agent and the transport plumbing.</summary>
public static class ManagementBuilderExtensions
{
    /// <summary>
    /// Registers a managed service: its identity, its operations' host, and the transports it
    /// announces on.
    /// </summary>
    public static IServiceCollection AddRatatoskrManagementAgent(
        this IServiceCollection services,
        Action<ManagementAgentBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddRatatoskrManagementCore();

        var builder = new ManagementAgentBuilder(services);
        configure(builder);
        builder.Apply();

        return services;
    }

    /// <summary>
    /// Registers the transport registry and the discovery consumer shared by every dashboard.
    /// </summary>
    internal static IServiceCollection AddManagementTransportRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ManagementDashboardOptions>();
        services.TryAddSingleton<IManagementTransportRegistry, ManagementTransportRegistry>();
        services.TryAddSingleton(sp => new ServiceRegistry(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<ManagementDashboardOptions>>().Value.StaleAfter
        ));
        services.TryAddSingleton<TransportManagementDispatchStrategy>();
        services.AddHostedService<ManagementDiscoveryService>();
        return services;
    }

    /// <summary>
    /// Registers the in-process transport. Callers gate this on the name reservation, because both
    /// sides of a co-hosted control plane register the same transport and only the first should
    /// actually add it.
    /// </summary>
    internal static IServiceCollection AddInProcessManagementTransport(
        this IServiceCollection services,
        string name
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddRatatoskrManagementCore();
        services.AddSingleton<IManagementTransport>(sp => new InProcessManagementTransport(
            name,
            sp.GetRequiredService<IManagementDispatcher>(),
            sp.GetRequiredService<IOptions<ManagementAgentOptions>>()
        ));
        services.AddSingleton<IManagementDiscoverySource>(sp => new InProcessManagementDiscoverySource(
            name,
            sp.GetRequiredService<ManagementAgent>(),
            sp.GetRequiredService<IOptions<ManagementAgentOptions>>(),
            sp.GetRequiredService<TimeProvider>()
        ));

        return services;
    }

    /// <summary>
    /// Returns the single instance of <typeparamref name="T"/> already registered in
    /// <paramref name="services"/>, adding one if this is the first call. Used for the small
    /// amount of state that has to be shared across several registration calls before the
    /// container exists.
    /// </summary>
    internal static T GetOrAddInstance<T>(this IServiceCollection services)
        where T : class, new()
    {
        var existing = services
            .Where(descriptor => descriptor.ServiceType == typeof(T))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<T>()
            .FirstOrDefault();

        if (existing is not null)
        {
            return existing;
        }

        var created = new T();
        services.AddSingleton(created);
        return created;
    }
}
