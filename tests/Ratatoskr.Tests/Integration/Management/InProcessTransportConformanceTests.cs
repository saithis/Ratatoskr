using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.Management;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>The conformance suite, over the in-process transport.</summary>
[InheritsTests]
public sealed class InProcessTransportConformanceTests : ManagementTransportConformanceTests
{
    protected override string TransportName => "in-process";

    protected override Task<ConformanceHost> StartAsync() =>
        InProcessControlPlane.StartAsync(
            TransportName,
            ConformanceServiceName,
            ConformanceInstanceId,
            TimeProvider.System
        );

    [Test]
    public async Task Announcements_ContinuePastTheStalenessWindow()
    {
        // The bug this pins used to be invisible: a single announcement at startup left a
        // co-hosted dashboard showing its own service until the staleness window elapsed, and
        // then showing nothing — an outage indistinguishable from a real one.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero));
        await using var host = await InProcessControlPlane.StartAsync(
            "in-process",
            ConformanceServiceName,
            ConformanceInstanceId,
            clock
        );

        await host.Client.WaitForServiceAsync("in-process", ConformanceServiceName);

        for (var beat = 0; beat < 6; beat++)
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            await Task.Delay(50);
        }

        var card = host.Client.Registry.GetServices().Single();
        card.Liveness.Should().Be(ServiceLiveness.Online, "the agent kept announcing");
        card.LastSeenAt.Should().BeOnOrAfter(clock.GetUtcNow() - TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task Transport_RefusesAServiceItDoesNotHost()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, ConformanceServiceName);

        // Address a service that was never announced, so the registry has no address for it and
        // the transport is asked to reach something that is not in this process.
        var response = await host.Client.SendAsync(
            TransportName,
            "some-other-service",
            EchoManagementOperation.OperationName,
            new EchoRequest()
        );

        response.IsSuccess.Should().BeFalse();
        response.Error!.Code.Should().Be(ManagementErrorCodes.TargetUnreachable);
    }
}

/// <summary>Builds an agent and a dashboard that share a process and a transport.</summary>
internal static class InProcessControlPlane
{
    public static async Task<ConformanceHost> StartAsync(
        string transportName,
        string serviceName,
        string instanceId,
        TimeProvider clock
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddSingleton<IManagementOperation>(new EchoManagementOperation());

        services.AddRatatoskrManagementAgent(agent =>
        {
            agent.ServiceName = serviceName;
            agent.InstanceId = instanceId;
            agent.Configure(options => options.HeartbeatInterval = TimeSpan.FromSeconds(5));
            agent.AddInProcess(transportName);
        });

        // The transport runtime without a store: this suite is about the transport, and the
        // dashboard's persistence is covered where it belongs, in the dashboard tests.
        var dashboard = new ManagementDashboardBuilder(services);
        dashboard.Apply();

        var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
        );

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        return new ConformanceHost(
            new ManagementTestClient(provider),
            async () =>
            {
                foreach (var service in hosted)
                {
                    await service.StopAsync(CancellationToken.None);
                }

                await provider.DisposeAsync();
            }
        );
    }
}
