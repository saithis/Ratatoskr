using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using InventoryService;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Ratatoskr;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;
using Ratatoskr.UI.Client;
using TUnit.Core;

namespace Ratatoskr.Tests.Examples;

[ClassDataSource<RabbitMqContainerFixture, PostgresContainerFixture>(
    Shared = [SharedType.PerTestSession, SharedType.PerTestSession]
)]
[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "Test lifecycle managed in DisposeAsync.")]
[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP003:Dispose previous before re-assigning", Justification = "One-time test assignment.")]
[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP004:Don't ignore created IDisposable", Justification = "Test response lifecycle.")]
public sealed class InventoryServiceManagementTests : IAsyncDisposable
{
    private readonly RabbitMqContainerFixture _rabbit;
    private readonly PostgresContainerFixture _postgres;
    private readonly string _testId = Guid.NewGuid().ToString("N");

    private WebApplicationFactory<InventoryServiceAppMarker>? _serviceFactory;
    private ServiceProvider? _uiProvider;
    private List<IHostedService> _uiHostedServices = [];

    public InventoryServiceManagementTests(
        RabbitMqContainerFixture rabbit,
        PostgresContainerFixture postgres
    )
    {
        _rabbit = rabbit;
        _postgres = postgres;
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed in DisposeAsync")]
    private async Task<(HttpClient Client, IRatatoskrBrokerManagementClient UiClient, string ServiceName, string QueuePrefix)> StartAsync()
    {
        var invDb = $"inv_{_testId}";
        var audDb = $"aud_{_testId}";
        var maint = MaintenanceConnectionString(_postgres.ConnectionString);
        await CreateDatabaseAsync(maint, invDb);
        await CreateDatabaseAsync(maint, audDb);

        var invCs = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = invDb, }.ToString();
        var audCs = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { Database = audDb, }.ToString();

        var serviceName = $"inv-{_testId}";
        var uiPrefix = $"ui-{_testId}";
        var queuePrefix = $"q_{_testId}";

        // 1. Build and start UI client on the same broker
        var uiServices = new ServiceCollection();
        uiServices.AddLogging();
        uiServices.AddRatatoskr(bus =>
        {
            bus.UseRabbitMq(o => o.ConnectionString = new Uri(_rabbit.ConnectionString));
        });
        uiServices.AddRatatoskrUI(o =>
        {
            o.UiExchangePrefix = uiPrefix;
            o.RequestTimeout = TimeSpan.FromSeconds(15);
        });

        _uiProvider = uiServices.BuildServiceProvider();
        _uiHostedServices = _uiProvider.GetServices<IHostedService>().ToList();
        foreach (var svc in _uiHostedServices)
        {
            await svc.StartAsync(CancellationToken.None);
        }

        // 2. Start InventoryService WebApplicationFactory
        var factory = new WebApplicationFactory<InventoryServiceAppMarker>().WithWebHostBuilder(
            builder =>
            {
                builder.UseSetting("ConnectionStrings:rabbitmq", _rabbit.ConnectionString);
                builder.UseSetting("ConnectionStrings:inventorydb", invCs);
                builder.UseSetting("ConnectionStrings:auditdb", audCs);
                builder.UseSetting("Ratatoskr:Management:ServiceName", serviceName);
                builder.UseSetting("Ratatoskr:Management:UiExchangePrefix", uiPrefix);
                builder.UseSetting("Inventory:QueuePrefix", queuePrefix);
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
            }
        );

        _serviceFactory = factory;
        _ = factory.Server; // ensure server startup

        var uiClient = _uiProvider.GetRequiredService<IRatatoskrBrokerManagementClient>();
        return (factory.CreateClient(), uiClient, serviceName, queuePrefix);
    }

    private static string MaintenanceConnectionString(string fixtureCs)
    {
        var b = new NpgsqlConnectionStringBuilder(fixtureCs) { Database = "postgres", };
        return b.ToString();
    }

    private static async Task CreateDatabaseAsync(string maintenanceConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            // already exists
        }
    }

    [Test]
    public async Task InventoryService_HeartbeatDiscoveredOverBroker_ShowsMultiDbContextWithAsymmetricInbox()
    {
        var (_, uiClient, serviceName, queuePrefix) = await StartAsync();

        ServiceDetailDto? discovered = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            discovered = uiClient.Registry.GetService(serviceName);
            if (discovered?.Status == "online")
            {
                break;
            }

            await Task.Delay(250);
        }

        discovered.Should().NotBeNull();
        discovered!.ServiceName.Should().Be(serviceName);
        discovered.Status.Should().Be("online");
        discovered.Instances.Should().NotBeEmpty();

        // Check DbContexts
        var invDb = discovered.DbContexts.FirstOrDefault(d => d.DbContextName == "InventoryDbContext");
        invDb.Should().NotBeNull();
        invDb!.HasOutbox.Should().BeTrue();
        invDb.HasInbox.Should().BeTrue();

        // AuditDbContext has Outbox only
        var audDb = discovered.DbContexts.FirstOrDefault(d => d.DbContextName == "AuditDbContext");
        audDb.Should().NotBeNull();
        audDb!.HasOutbox.Should().BeTrue();
        audDb.HasInbox.Should().BeFalse();

        // Check Channels
        discovered.Channels.Should().NotBeEmpty();
        discovered.Channels.Should().Contain(c => c.ChannelName == $"{queuePrefix}.commands");
        discovered.Channels.Should().Contain(c => c.ChannelName == $"{queuePrefix}.audit");
    }

    [Test]
    public async Task InventoryService_SimulateFailure_PoisonedInboxRowCanBeInspectedAndRequeuedOverBroker()
    {
        var (client, uiClient, serviceName, _) = await StartAsync();

        // Ensure service has announced itself
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (uiClient.Registry.GetService(serviceName)?.Status == "online")
            {
                break;
            }

            await Task.Delay(250);
        }

        // Trigger failing reservation
        using var postResp = await client.PostAsync("/inventory/reservations/simulate-failure", content: null);
        postResp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Wait for inbox message to exhaust retries and become poisoned
        PagedResult<InboxItemDto>? inboxResult = null;
        var poisonDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < poisonDeadline)
        {
            try
            {
                inboxResult = await uiClient.ExecuteAsync<GetInboxMessagesRequest, PagedResult<InboxItemDto>>(
                    serviceName,
                    "InventoryDbContext",
                    "GetInbox",
                    new GetInboxMessagesRequest(Status: "Poisoned", Page: 1, PageSize: 10)
                );

                if (inboxResult is { TotalCount: > 0 })
                {
                    break;
                }
            }
            catch
            {
                // broker queue may not have consumed yet
            }

            await Task.Delay(500);
        }

        inboxResult.Should().NotBeNull();
        inboxResult!.TotalCount.Should().BeGreaterThanOrEqualTo(1);

        var poisonedItem = inboxResult.Items[0];
        poisonedItem.IsPoisoned.Should().BeTrue();
        poisonedItem.HandlerKey.Should().Be("inventory.reserve-stock");
        poisonedItem.LastError.Should().Contain("Simulated stock reservation failure");

        // Inspect detail over broker RPC
        var detailResult = await uiClient.ExecuteAsync<GetInboxDetailRequest, InboxDetailDto>(
            serviceName,
            "InventoryDbContext",
            "GetInboxDetail",
            new GetInboxDetailRequest(poisonedItem.Id)
        );

        detailResult.Should().NotBeNull();
        detailResult!.Id.Should().Be(poisonedItem.Id);
        detailResult.Content.Should().NotBeNull();

        // Requeue handler over broker RPC
        var requeueResult = await uiClient.ExecuteAsync<RequeueInboxHandlerRequest, RequeueResultDto>(
            serviceName,
            "InventoryDbContext",
            "RequeueInboxHandler",
            new RequeueInboxHandlerRequest(poisonedItem.Id)
        );

        requeueResult.Should().NotBeNull();
        requeueResult!.RequeuedCount.Should().Be(1);

        // Verify it is no longer poisoned
        var inboxAfter = await uiClient.ExecuteAsync<GetInboxMessagesRequest, PagedResult<InboxItemDto>>(
            serviceName,
            "InventoryDbContext",
            "GetInbox",
            new GetInboxMessagesRequest(Status: "Poisoned", Page: 1, PageSize: 10)
        );

        inboxAfter.Should().NotBeNull();
        inboxAfter!.TotalCount.Should().Be(0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceFactory is not null)
        {
            await _serviceFactory.DisposeAsync();
            _serviceFactory = null;
        }

        foreach (var svc in _uiHostedServices)
        {
            await svc.StopAsync(CancellationToken.None);
        }

        if (_uiProvider is not null)
        {
            await _uiProvider.DisposeAsync();
            _uiProvider = null;
        }
    }
}
