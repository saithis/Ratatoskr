using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Transports;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>Adds RabbitMQ control planes to a management agent or a dashboard.</summary>
public static class RabbitMqManagementBuilderExtensions
{
    /// <summary>The transport kind reported in duplicate-name errors.</summary>
    public const string Kind = "rabbitmq";

    /// <summary>
    /// Serves management commands for this service over a RabbitMQ control plane, and announces
    /// it to the dashboard that owns <see cref="RabbitMqManagementOptions.DiscoveryExchange"/>.
    /// </summary>
    public static ManagementAgentBuilder AddRabbitMq(
        this ManagementAgentBuilder builder,
        string name,
        Action<RabbitMqManagementOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Both sides of a co-hosted control plane may register the same name — the agent to serve
        // commands, the dashboard to watch — so the name is claimed per side, not per call.
        builder.TransportNames.ReserveRole(
            name,
            Kind,
            ManagementTransportNameReservations.AgentRole
        );
        Register(builder.Services, name, configure);

        // An agent serves commands, so it is the side that has to authenticate its callers; the
        // validator refuses to start a process that would accept anything off the broker.
        builder.Services.AddSingleton<IValidateOptions<RabbitMqManagementOptions>>(
            new RabbitMqManagementAgentSecurityValidator(name)
        );

        builder.Services.AddSingleton<IHostedService>(sp => new RabbitMqManagementAgentService(
            name,
            sp.GetRequiredKeyedService<RabbitMqManagementConnection>(name),
            sp.GetRequiredService<IOptionsMonitor<RabbitMqManagementOptions>>(),
            sp.GetRequiredService<IManagementDispatcher>(),
            sp.GetRequiredService<ManagementAgent>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RabbitMqManagementAgentService>>()
        ));

        return builder;
    }

    /// <summary>Watches one RabbitMQ control plane from a dashboard.</summary>
    public static ManagementDashboardBuilder AddRabbitMq(
        this ManagementDashboardBuilder builder,
        string name,
        Action<RabbitMqManagementOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.TransportNames.ReserveRole(
            name,
            Kind,
            ManagementTransportNameReservations.DashboardRole
        );
        Register(builder.Services, name, configure);

        // The transport is both the thing the dashboard sends through and a hosted service that
        // owns the reply consumer, so it is registered once and surfaced twice.
        builder.Services.AddKeyedSingleton(
            name,
            (sp, key) => new RabbitMqManagementTransport(
                (string)key!,
                sp.GetRequiredKeyedService<RabbitMqManagementConnection>(key),
                sp.GetRequiredService<IOptionsMonitor<RabbitMqManagementOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<RabbitMqManagementTransport>>()
            )
        );
        builder.Services.AddSingleton<IManagementTransport>(sp =>
            sp.GetRequiredKeyedService<RabbitMqManagementTransport>(name)
        );
        builder.Services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredKeyedService<RabbitMqManagementTransport>(name)
        );

        builder.Services.AddSingleton<IManagementDiscoverySource>(sp =>
            new RabbitMqManagementDiscoverySource(
                name,
                sp.GetRequiredKeyedService<RabbitMqManagementConnection>(name),
                sp.GetRequiredService<IOptionsMonitor<RabbitMqManagementOptions>>(),
                sp.GetRequiredService<ILogger<RabbitMqManagementDiscoverySource>>()
            )
        );

        return builder;
    }

    private static void Register(
        IServiceCollection services,
        string name,
        Action<RabbitMqManagementOptions> configure
    )
    {
        services.AddOptions<RabbitMqManagementOptions>(name).Configure(configure).ValidateOnStart();
        services.TryAddEnumerableSingleton<
            IValidateOptions<RabbitMqManagementOptions>,
            RabbitMqManagementOptionsValidator
        >();

        services.TryAddSingleton(TimeProvider.System);

        // One connection per control plane, shared by whichever sides of it this process runs.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddKeyedSingleton(
            services,
            typeof(RabbitMqManagementConnection),
            name,
            (sp, key) => new RabbitMqManagementConnection(
                (string)key!,
                sp.GetRequiredService<IOptionsMonitor<RabbitMqManagementOptions>>().Get((string)key!)
            )
        );
    }

    private static void TryAddEnumerableSingleton<TService, TImplementation>(
        this IServiceCollection services
    )
        where TService : class
        where TImplementation : class, TService =>
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(
            services,
            ServiceDescriptor.Singleton<TService, TImplementation>()
        );

    private static void TryAddSingleton<TService>(this IServiceCollection services, TService instance)
        where TService : class =>
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(
            services,
            instance
        );
}
