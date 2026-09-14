using System.Diagnostics.CodeAnalysis;
using System.Net;
using AwesomeAssertions;
using InventoryService;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Ratatoskr.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.RabbitMq;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;

namespace Ratatoskr.Tests.Examples;

/// <summary>
/// The real distributed shape: a dashboard in one process reaching the InventoryService example in
/// another, over a broker, with nothing shared but the connection string.
/// </summary>
[ClassDataSource<RabbitMqContainerFixture, PostgresContainerFixture>(
    Shared = [SharedType.PerTestSession, SharedType.PerTestSession]
)]
[SuppressMessage(
    "IDisposableAnalyzers.Correctness",
    "IDISP001:Dispose created",
    Justification = "Test lifecycle is managed in DisposeAsync."
)]
[SuppressMessage(
    "IDisposableAnalyzers.Correctness",
    "IDISP003:Dispose previous before re-assigning",
    Justification = "One-time test assignment."
)]
[SuppressMessage(
    "IDisposableAnalyzers.Correctness",
    "IDISP004:Don't ignore created IDisposable",
    Justification = "The HttpClient is owned by the WebApplicationFactory, which is disposed in DisposeAsync."
)]
public sealed class InventoryServiceManagementTests(
    RabbitMqContainerFixture rabbit,
    PostgresContainerFixture postgres
) : IAsyncDisposable
{
    private const string TransportName = "broker";

    private readonly string _testId = Guid.NewGuid().ToString("N");

    private WebApplicationFactory<InventoryServiceAppMarker>? _serviceFactory;
    private ServiceProvider? _dashboard;
    private List<IHostedService> _dashboardServices = [];

    [Test]
    public async Task Dashboard_DiscoversTheServiceAndItsAsymmetricContexts()
    {
        var context = await StartAsync();

        var detail = await context.Client.WaitForServiceAsync(TransportName, context.ServiceName);

        detail.ServiceName.Should().Be(context.ServiceName);
        detail.Liveness.Should().Be(ServiceLivenessOnline);
        detail.Instances.Should().NotBeEmpty();

        var inventory = detail.DbContexts.Single(d => d.Name == "InventoryDbContext");
        inventory.HasOutbox.Should().BeTrue();
        inventory.HasInbox.Should().BeTrue();

        // The audit context has an outbox but no inbox, which is exactly the asymmetry a
        // capability-blind dashboard would render wrongly.
        var audit = detail.DbContexts.Single(d => d.Name == "AuditDbContext");
        audit.HasOutbox.Should().BeTrue();
        audit.HasInbox.Should().BeFalse();

        detail.Channels.Should().Contain(c => c.LogicalName == $"{context.QueuePrefix}.commands");
        detail.Channels.Should().Contain(c => c.LogicalName == $"{context.QueuePrefix}.audit");

        detail.Capabilities.Select(c => c.Name)
            .Should()
            .Contain([ManagementCapabilityNames.Outbox, ManagementCapabilityNames.Inbox]);
    }

    [Test]
    public async Task Dashboard_InspectsAndRequeuesAPoisonedHandlerOverTheBroker()
    {
        var context = await StartAsync();
        await context.Client.WaitForServiceAsync(TransportName, context.ServiceName);

        using var triggered = await context.Http.PostAsync(
            "/inventory/reservations/simulate-failure",
            content: null
        );
        triggered.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var poisoned = await WaitForPoisonedHandlerAsync(context);
        poisoned.HandlerKey.Should().Be("inventory.reserve-stock");
        poisoned.LastError.Should().Contain("Simulated stock reservation failure");

        var detail = await context.Client.ExecuteAsync<InboxDetail>(
            TransportName,
            context.ServiceName,
            ManagementOperationNames.InboxGet,
            new GetMessageRequest(poisoned.HandlerStatusId),
            resource: "InventoryDbContext"
        );
        detail.HandlerStatusId.Should().Be(poisoned.HandlerStatusId);
        detail.JsonPayload.Should().NotBeNull();

        var requeued = await context.Client.ExecuteAsync<MutationResponse>(
            TransportName,
            context.ServiceName,
            ManagementOperationNames.InboxRequeue,
            new MutateByIdsRequest { Ids = [poisoned.HandlerStatusId] },
            resource: "InventoryDbContext"
        );
        requeued.Succeeded.Should().BeEquivalentTo([poisoned.HandlerStatusId]);

        var after = await context.Client.ExecuteAsync<MessageCountResponse>(
            TransportName,
            context.ServiceName,
            ManagementOperationNames.InboxCount,
            new CountMessagesRequest(),
            resource: "InventoryDbContext"
        );
        after.Count.Should().Be(0);
    }

    [Test]
    public async Task Dashboard_TargetingAnUnknownReplica_FailsFastRatherThanTimingOut()
    {
        var context = await StartAsync();
        await context.Client.WaitForServiceAsync(TransportName, context.ServiceName);

        var started = DateTime.UtcNow;
        var response = await context.Client.SendAsync(
            TransportName,
            context.ServiceName,
            ManagementOperationNames.ContextsList,
            new ListContextsRequest(),
            instanceId: "a-replica-that-never-existed",
            timeout: TimeSpan.FromSeconds(30)
        );

        response.IsSuccess.Should().BeFalse();
        response.Error!.Code.Should().Be(ManagementErrorCodes.TargetUnreachable);
        (DateTime.UtcNow - started)
            .Should()
            .BeLessThan(
                TimeSpan.FromSeconds(15),
                "an unreachable instance must fail fast, not wait out the deadline"
            );
    }

    private static Ratatoskr.Management.Registry.ServiceLiveness ServiceLivenessOnline =>
        Ratatoskr.Management.Registry.ServiceLiveness.Online;

    private static async Task<InboxListItem> WaitForPoisonedHandlerAsync(TestContext context)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var page = await context.Client.ExecuteAsync<CursorPage<InboxListItem>>(
                TransportName,
                context.ServiceName,
                ManagementOperationNames.InboxList,
                new ListMessagesRequest { Limit = 10 },
                resource: "InventoryDbContext"
            );

            if (page.Items.Count > 0)
            {
                return page.Items[0];
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("No poisoned inbox handler appeared within the timeout.");
    }

    private sealed record TestContext(
        HttpClient Http,
        ManagementTestClient Client,
        string ServiceName,
        string QueuePrefix
    );

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Disposed in DisposeAsync."
    )]
    private async Task<TestContext> StartAsync()
    {
        var inventoryDb = $"inv_{_testId}";
        var auditDb = $"aud_{_testId}";
        var dashboardDb = $"dash_{_testId}";
        var maintenance = MaintenanceConnectionString(postgres.ConnectionString);
        await CreateDatabaseAsync(maintenance, inventoryDb);
        await CreateDatabaseAsync(maintenance, auditDb);
        await CreateDatabaseAsync(maintenance, dashboardDb);

        var serviceName = $"inv-{_testId}";
        var servicePrefix = $"svc{_testId[..8]}";
        var dashboardPrefix = $"dash{_testId[..8]}";
        var queuePrefix = $"q_{_testId}";

        var dashboardServices = new ServiceCollection();
        dashboardServices.AddLogging();
        dashboardServices.AddSingleton(TimeProvider.System);
        dashboardServices.AddRatatoskrDashboard(dashboard =>
        {
            dashboard.UseStore(
                db => db.UseNpgsql(ConnectionStringFor(dashboardDb)),
                store => store.AutoMigrate = true
            );
            dashboard.Configure(options => options.StaleAfter = TimeSpan.FromSeconds(45));
            dashboard.AddRabbitMq(
                TransportName,
                options =>
                {
                    options.ConnectionString = new Uri(rabbit.ConnectionString);
                    options.ResourcePrefix = dashboardPrefix;
                    options.ReplicaId = "r1";
                    options.HeartbeatInterval = TimeSpan.FromSeconds(2);
                }
            );
        });

        _dashboard = dashboardServices.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
        );
        _dashboardServices = _dashboard.GetServices<IHostedService>().ToList();
        foreach (var hosted in _dashboardServices)
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        var factory = new WebApplicationFactory<InventoryServiceAppMarker>().WithWebHostBuilder(
            builder =>
            {
                builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
                builder.UseSetting("ConnectionStrings:inventorydb", ConnectionStringFor(inventoryDb));
                builder.UseSetting("ConnectionStrings:auditdb", ConnectionStringFor(auditDb));
                builder.UseSetting("Ratatoskr:Management:ServiceName", serviceName);
                builder.UseSetting("Ratatoskr:Management:ResourcePrefix", servicePrefix);
                builder.UseSetting(
                    "Ratatoskr:Management:DiscoveryExchange",
                    $"{dashboardPrefix}.mgmt.discovery.inbox"
                );
                builder.UseSetting("Inventory:QueuePrefix", queuePrefix);
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
            }
        );

        _serviceFactory = factory;
        _ = factory.Server;

        return new TestContext(
            factory.CreateClient(),
            new ManagementTestClient(_dashboard),
            serviceName,
            queuePrefix
        );
    }

    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = database }.ToString();

    private static string MaintenanceConnectionString(string fixtureConnectionString) =>
        new NpgsqlConnectionStringBuilder(fixtureConnectionString) { Database = "postgres" }.ToString();

    private static async Task CreateDatabaseAsync(string maintenance, string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenance);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            // Already exists.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceFactory is not null)
        {
            await _serviceFactory.DisposeAsync();
            _serviceFactory = null;
        }

        foreach (var hosted in _dashboardServices)
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        if (_dashboard is not null)
        {
            await _dashboard.DisposeAsync();
            _dashboard = null;
        }
    }
}
