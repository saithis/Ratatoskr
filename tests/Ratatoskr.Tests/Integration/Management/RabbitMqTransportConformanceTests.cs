using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// The conformance suite, over RabbitMQ — and over a broker whose identities carry exactly the
/// least-privilege permissions from <c>docs/rabbitmq.md</c>, so passing here means passing on a
/// properly locked-down broker rather than on an administrator connection.
/// </summary>
[InheritsTests]
[ClassDataSource<RestrictedRabbitMqFixture>(Shared = SharedType.PerTestSession)]
public sealed class RabbitMqTransportConformanceTests(RestrictedRabbitMqFixture broker)
    : ManagementTransportConformanceTests
{
    protected override string TransportName => "broker";

    protected override async Task<ConformanceHost> StartAsync()
    {
        // A unique replica id per test keeps each run's exclusive reply and discovery queues to
        // itself, so tests sharing the session's broker cannot steal each other's messages.
        var replicaId = $"r{Guid.NewGuid().ToString("N")[..8]}";
        var serviceName = $"conf-{Guid.NewGuid().ToString("N")[..10]}";
        var instanceId = $"inst-{Guid.NewGuid().ToString("N")[..10]}";

        var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            TransportName,
            replicaId
        );

        var agent = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            TransportName,
            serviceName,
            instanceId,
            dashboard.DiscoveryExchange
        );

        return new ConformanceHost(
            dashboard.Client,
            serviceName,
            instanceId,
            async () =>
            {
                await agent.DisposeAsync();
                await dashboard.DisposeAsync();
            }
        );
    }
}
