using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Ratatoskr.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.RabbitMq;
using Ratatoskr.Management.Registry;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// The RabbitMQ control plane's own behaviour: the parts of the design that only exist because of
/// how the broker and its permission model actually work.
/// </summary>
[ClassDataSource<RestrictedRabbitMqFixture>(Shared = SharedType.PerTestSession)]
public sealed class RabbitMqControlPlaneTests(RestrictedRabbitMqFixture broker)
{
    private const string Transport = "broker";

    private static string NewReplicaId() => $"r{Guid.NewGuid().ToString("N")[..8]}";

    private static string NewServiceName() => $"svc{Guid.NewGuid().ToString("N")[..8]}";

    [Test]
    public async Task TwoDashboardReplicas_BothSeeEveryHeartbeatAndKeepTheirOwnReplies()
    {
        // A shared discovery queue would make replicas compete, so each heartbeat would reach one
        // of them and the others would show a fleet that keeps going stale for no reason. A shared
        // reply queue would be worse: one replica would answer another's request.
        var serviceName = NewServiceName();

        await using var first = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId()
        );
        await using var second = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId()
        );
        await using var agent = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-1",
            first.DiscoveryExchange
        );

        await first.Client.WaitForServiceAsync(Transport, serviceName);
        await second.Client.WaitForServiceAsync(Transport, serviceName);

        var fromFirst = await first.Client.ExecuteAsync<EchoResponse>(
            Transport,
            serviceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "for-first" }
        );
        var fromSecond = await second.Client.ExecuteAsync<EchoResponse>(
            Transport,
            serviceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "for-second" }
        );

        fromFirst.Message.Should().Be("for-first");
        fromSecond.Message.Should().Be("for-second");
    }

    [Test]
    public async Task TwoReplicasOfOneService_CompeteForLogicalCommands()
    {
        // Replicas share the durable service queue, so exactly one handles each logical command —
        // and a replica that dies takes no traffic with it.
        var serviceName = NewServiceName();
        var firstEcho = new EchoManagementOperation();
        var secondEcho = new EchoManagementOperation();

        await using var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId()
        );
        await using var replicaOne = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-1",
            dashboard.DiscoveryExchange,
            echo: firstEcho
        );
        await using var replicaTwo = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-2",
            dashboard.DiscoveryExchange,
            echo: secondEcho
        );

        var detail = await dashboard.Client.WaitForServiceAsync(Transport, serviceName);
        await WaitForAsync(
            () => dashboard.Client.Registry.GetService(Transport, serviceName)!.Instances.Count == 2,
            "both replicas were discovered"
        );

        for (var index = 0; index < 12; index++)
        {
            await dashboard.Client.ExecuteAsync<EchoResponse>(
                Transport,
                serviceName,
                EchoManagementOperation.OperationName,
                new EchoRequest { Message = $"command-{index}" }
            );
        }

        (firstEcho.Invocations + secondEcho.Invocations)
            .Should()
            .Be(12, "each logical command is handled exactly once");
        detail.ServiceName.Should().Be(serviceName);
    }

    [Test]
    public async Task InstanceTargeting_ReachesThatReplicaAndNoOther()
    {
        var serviceName = NewServiceName();
        var firstEcho = new EchoManagementOperation();
        var secondEcho = new EchoManagementOperation();

        await using var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId()
        );
        await using var replicaOne = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-1",
            dashboard.DiscoveryExchange,
            echo: firstEcho
        );
        await using var replicaTwo = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-2",
            dashboard.DiscoveryExchange,
            echo: secondEcho
        );

        await dashboard.Client.WaitForServiceAsync(Transport, serviceName);
        await WaitForAsync(
            () => dashboard.Client.Registry.GetService(Transport, serviceName)!.Instances.Count == 2,
            "both replicas were discovered"
        );

        for (var index = 0; index < 5; index++)
        {
            await dashboard.Client.ExecuteAsync<EchoResponse>(
                Transport,
                serviceName,
                EchoManagementOperation.OperationName,
                new EchoRequest { Message = "targeted" },
                instanceId: "instance-2"
            );
        }

        secondEcho.Invocations.Should().Be(5);
        firstEcho.Invocations.Should().Be(0);
    }

    [Test]
    public async Task AgentStartedBeforeItsDashboard_KeepsServingAndIsDiscoveredLater()
    {
        // A publisher cannot declare a receiver-owned exchange, so an agent that starts first will
        // publish into an exchange that does not exist, get a 404, and lose its heartbeat channel.
        // That is a boot-ordering state, not a failure: command serving must be unaffected, and
        // discovery must recover on its own once the dashboard shows up.
        var serviceName = NewServiceName();
        var replicaId = NewReplicaId();
        var discoveryExchange = $"{RestrictedRabbitMqFixture.DashboardIdentity}.mgmt.discovery.inbox";

        await using var agent = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-1",
            discoveryExchange
        );

        // Let the agent try and fail to announce for a while before the dashboard exists.
        await Task.Delay(TimeSpan.FromSeconds(3));

        await using var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            replicaId
        );

        var detail = await dashboard.Client.WaitForServiceAsync(
            Transport,
            serviceName,
            TimeSpan.FromSeconds(60)
        );
        detail.Liveness.Should().Be(ServiceLiveness.Online);

        var echoed = await dashboard.Client.ExecuteAsync<EchoResponse>(
            Transport,
            serviceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "still serving" }
        );
        echoed.Message.Should().Be("still serving");
    }

    [Test]
    public async Task CommandFromAnUnknownIdentity_IsRefusedBeforeDispatch()
    {
        // The broker delivers it happily: write access to '*.inbox' is granted to every identity in
        // the vhost. The agent is the only thing standing between an attacker and another
        // service's outbox.
        var serviceName = NewServiceName();
        var echo = new EchoManagementOperation();

        await using var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId()
        );
        await using var agent = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-1",
            dashboard.DiscoveryExchange,
            allowedCallers: [RestrictedRabbitMqFixture.DashboardIdentity],
            echo: echo
        );

        await dashboard.Client.WaitForServiceAsync(Transport, serviceName);

        await InjectCommandAsync(
            RestrictedRabbitMqFixture.AttackerIdentity,
            $"{RestrictedRabbitMqFixture.ServiceIdentity}.mgmt.cmd.inbox",
            $"svc.{serviceName}",
            setUserId: false
        );
        await InjectCommandAsync(
            RestrictedRabbitMqFixture.AttackerIdentity,
            $"{RestrictedRabbitMqFixture.ServiceIdentity}.mgmt.cmd.inbox",
            $"svc.{serviceName}",
            setUserId: true
        );

        // Prove the agent is alive and consuming by driving a legitimate command through after
        // the injected ones: if the injected commands had been dispatched, this count would be
        // higher than one.
        await dashboard.Client.ExecuteAsync<EchoResponse>(
            Transport,
            serviceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "legitimate" }
        );

        echo.Invocations.Should().Be(1, "only the allowlisted caller's command was dispatched");
    }

    [Test]
    public async Task SharedIdentityDeployment_AuthenticatesCallersWithASignature()
    {
        // Where several parties authenticate as one RabbitMQ user, user_id no longer distinguishes
        // them, so the envelope is signed instead.
        var serviceName = NewServiceName();
        const string secret = "a-shared-management-secret";

        await using var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId(),
            sharedSecret: secret
        );
        await using var agent = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-1",
            dashboard.DiscoveryExchange,
            sharedSecret: secret
        );

        await dashboard.Client.WaitForServiceAsync(Transport, serviceName);

        var echoed = await dashboard.Client.ExecuteAsync<EchoResponse>(
            Transport,
            serviceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "signed" }
        );

        echoed.Message.Should().Be("signed");
    }

    [Test]
    public async Task SharedIdentityDeployment_RefusesAnUnsignedCommand()
    {
        var serviceName = NewServiceName();
        const string secret = "a-shared-management-secret";
        var echo = new EchoManagementOperation();

        await using var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId()
        );
        await using var agent = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            serviceName,
            "instance-1",
            dashboard.DiscoveryExchange,
            sharedSecret: secret,
            echo: echo
        );

        await dashboard.Client.WaitForServiceAsync(Transport, serviceName);

        // The dashboard has no secret configured, so its commands carry no signature.
        var response = await dashboard.Client.SendAsync(
            Transport,
            serviceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "unsigned" },
            timeout: TimeSpan.FromSeconds(10)
        );

        response.IsSuccess.Should().BeFalse();
        response.Error!.Code.Should().Be(ManagementErrorCodes.UnauthenticatedCaller);
        echo.Invocations.Should().Be(0);
    }

    [Test]
    public async Task TwoServicesSharingOneIdentity_StayIndependentlyAddressable()
    {
        // Deployment identity models vary: a prefix is not a service. Service and instance live in
        // routing keys, so several services behind one RabbitMQ user remain distinct.
        var firstService = NewServiceName();
        var secondService = NewServiceName();
        var firstEcho = new EchoManagementOperation();
        var secondEcho = new EchoManagementOperation();

        await using var dashboard = await RabbitMqControlPlane.StartDashboardAsync(
            broker,
            Transport,
            NewReplicaId()
        );
        await using var agentOne = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            firstService,
            "instance-1",
            dashboard.DiscoveryExchange,
            echo: firstEcho
        );
        await using var agentTwo = await RabbitMqControlPlane.StartAgentAsync(
            broker,
            Transport,
            secondService,
            "instance-1",
            dashboard.DiscoveryExchange,
            echo: secondEcho
        );

        await dashboard.Client.WaitForServiceAsync(Transport, firstService);
        await dashboard.Client.WaitForServiceAsync(Transport, secondService);

        await dashboard.Client.ExecuteAsync<EchoResponse>(
            Transport,
            firstService,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "one" }
        );

        firstEcho.Invocations.Should().Be(1);
        secondEcho.Invocations.Should().Be(0);
    }

    [Test]
    public async Task Agent_WithNoCallerAuthentication_RefusesToStart()
    {
        // "Accept anything from the broker" must never be reachable by omission.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddRatatoskrManagementAgent(agent =>
        {
            agent.ServiceName = NewServiceName();
            agent.InstanceId = "instance-1";
            agent.AddRabbitMq(
                Transport,
                options =>
                {
                    options.ConnectionString = new Uri(
                        broker.ConnectionStringFor(RestrictedRabbitMqFixture.ServiceIdentity)
                    );
                    options.ResourcePrefix = RestrictedRabbitMqFixture.ServiceIdentity;
                }
            );
        });

        await using var provider = services.BuildServiceProvider();

        var act = () =>
            provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RabbitMqManagementOptions>>()
                .Get(Transport);

        act.Should()
            .Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*AllowedCallers*");
    }

    [Test]
    public void TwoKindsOfTransportSharingAName_AreRefusedAtRegistration()
    {
        // A name identifies one control plane. Two different kinds behind one name would make the
        // dashboard's routes and its audit records ambiguous about which broker was reached.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        var act = () =>
            services.AddRatatoskrManagementAgent(agent =>
            {
                agent.ServiceName = "duplicated";
                agent.InstanceId = "instance-1";
                agent.AddInProcess(Transport);
                agent.AddRabbitMq(
                    Transport,
                    options =>
                        options.ConnectionString = new Uri(
                            broker.ConnectionStringFor(RestrictedRabbitMqFixture.ServiceIdentity)
                        )
                );
            });

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{Transport}'*");
    }

    [Test]
    public void RegisteringTheSameSideTwice_IsRefusedAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        var act = () =>
            services.AddRatatoskrManagementAgent(agent =>
            {
                agent.ServiceName = "duplicated";
                agent.InstanceId = "instance-1";
                agent.AddInProcess(Transport);
                agent.AddInProcess(Transport);
            });

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered as an agent*");
    }

    [Test]
    public void ManagementTransport_DoesNotDependOnTheMessagingStack()
    {
        // A dashboard process must be able to run with no messaging stack installed at all, which
        // is only true while this package does not reference it.
        var referenced = typeof(RabbitMqManagementBuilderExtensions)
            .Assembly.GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        referenced.Should().NotContain("Ratatoskr.RabbitMq");
        referenced.Should().NotContain("Ratatoskr.EfCore");
    }

    private async Task InjectCommandAsync(
        string identity,
        string exchange,
        string routingKey,
        bool setUserId
    )
    {
        var factory = new ConnectionFactory { Uri = new Uri(broker.ConnectionStringFor(identity)) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true
            )
        );

        var envelope = new ManagementRequestEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = Guid.NewGuid().ToString("N"),
            OperationId = Guid.NewGuid(),
            Target = new ManagementTarget("anything"),
            Operation = EchoManagementOperation.OperationName,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(30),
            Payload = ManagementJson.ToElement(new EchoRequest { Message = "injected" }),
        };

        await channel.BasicPublishAsync(
            exchange,
            routingKey,
            mandatory: false,
            basicProperties: new BasicProperties
            {
                CorrelationId = envelope.RequestId,
                MessageId = envelope.OperationId.ToString("N"),
                ContentType = "application/json",
                // The broker refuses a spoofed user_id outright, so the worst an attacker can do
                // is present its own — which is exactly what the allowlist is there to reject.
                UserId = setUserId ? identity : null,
            },
            body: Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(envelope, ManagementJson.Options)
            )
        );

        // Give the agent time to receive and refuse it before the test moves on.
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting until {because}.");
    }
}
