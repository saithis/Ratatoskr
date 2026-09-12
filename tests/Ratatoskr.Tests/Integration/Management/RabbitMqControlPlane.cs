using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.RabbitMq;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Builds agent and dashboard processes on a RabbitMQ control plane, under the least-privilege
/// permission patterns Ratatoskr promises to work with.
/// </summary>
/// <remarks>
/// Every identity here has exactly the permissions from <c>docs/rabbitmq.md</c> and nothing more,
/// so a test that passes is a test that would pass against a properly locked-down broker.
/// </remarks>
internal static class RabbitMqControlPlane
{
    /// <summary>One running agent process.</summary>
    internal sealed class Agent(ServiceProvider provider, IReadOnlyList<IHostedService> hosted)
        : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public async ValueTask DisposeAsync()
        {
            foreach (var service in hosted.Reverse())
            {
                await service.StopAsync(CancellationToken.None);
            }

            await Provider.DisposeAsync();
        }
    }

    /// <summary>One running dashboard process.</summary>
    internal sealed class Dashboard(
        ServiceProvider provider,
        IReadOnlyList<IHostedService> hosted,
        string prefix
    ) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public ManagementTestClient Client { get; } = new(provider);

        /// <summary>The exchange agents publish their heartbeats to.</summary>
        public string DiscoveryExchange { get; } = $"{prefix}.mgmt.discovery.inbox";

        public async ValueTask DisposeAsync()
        {
            foreach (var service in hosted.Reverse())
            {
                await service.StopAsync(CancellationToken.None);
            }

            await Provider.DisposeAsync();
        }
    }

    public static async Task<Dashboard> StartDashboardAsync(
        RestrictedRabbitMqFixture broker,
        string transportName,
        string replicaId = "r1",
        string identity = RestrictedRabbitMqFixture.DashboardIdentity,
        string? sharedSecret = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        var builder = new ManagementDashboardBuilder(services);
        builder.AddRabbitMq(
            transportName,
            options =>
            {
                options.ConnectionString = new Uri(broker.ConnectionStringFor(identity));
                options.ResourcePrefix = identity;
                options.ReplicaId = replicaId;
                options.HeartbeatInterval = TimeSpan.FromSeconds(1);
                options.SharedSecret = sharedSecret;
            }
        );
        builder.Configure(options => options.StaleAfter = TimeSpan.FromSeconds(10));
        builder.Apply();

        var provider = Build(services);
        var hosted = await StartAsync(provider);
        return new Dashboard(provider, hosted, identity);
    }

    public static async Task<Agent> StartAgentAsync(
        RestrictedRabbitMqFixture broker,
        string transportName,
        string serviceName,
        string instanceId,
        string discoveryExchange,
        string identity = RestrictedRabbitMqFixture.ServiceIdentity,
        IEnumerable<string>? allowedCallers = null,
        string? sharedSecret = null,
        EchoManagementOperation? echo = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IManagementOperation>(echo ?? new EchoManagementOperation());

        services.AddRatatoskrManagementAgent(agent =>
        {
            agent.ServiceName = serviceName;
            agent.InstanceId = instanceId;
            agent.Configure(options => options.HeartbeatInterval = TimeSpan.FromSeconds(1));
            agent.AddRabbitMq(
                transportName,
                options =>
                {
                    options.ConnectionString = new Uri(broker.ConnectionStringFor(identity));
                    options.ResourcePrefix = identity;
                    options.DiscoveryExchange = discoveryExchange;
                    options.HeartbeatInterval = TimeSpan.FromSeconds(1);
                    options.SharedSecret = sharedSecret;

                    foreach (var caller in allowedCallers ?? [RestrictedRabbitMqFixture.DashboardIdentity])
                    {
                        options.AllowedCallers.Add(caller);
                    }
                }
            );
        });

        var provider = Build(services);
        var hosted = await StartAsync(provider);
        return new Agent(provider, hosted);
    }

    private static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
        );

    private static async Task<IReadOnlyList<IHostedService>> StartAsync(ServiceProvider provider)
    {
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        return hosted;
    }
}
