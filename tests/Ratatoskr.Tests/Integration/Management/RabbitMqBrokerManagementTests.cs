using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;
using Ratatoskr.UI.Client;
using TUnit.Core;

namespace Ratatoskr.Tests.Integration.Management;

public class RabbitMqBrokerManagementTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    [Test]
    public async Task RabbitMq_DistributedTopology_DiscoversServices_AndExecutesRpcOverBroker()
    {
        await CreateDatabaseAsync();
        await EnsureTestDatabaseSchemaAsync();

        var uiPrefix = $"test.ui.{TestId}";
        var serviceName = "orders-service";

        // Seed a poisoned outbox message in the database for the orders service
        var poisonedId = Guid.Empty;
        await InScopeDatabaseAsync(async db =>
        {
            var time = TimeProvider.System;
            var props = new MessageProperties
            {
                Id = Guid.NewGuid().ToString(),
                Type = "orders.order-created",
                Source = serviceName,
            };
            var content = JsonSerializer.SerializeToUtf8Bytes(new { OrderId = 42, Amount = 99.95 });
            var entity = OutboxMessageEntity.Create(content, props, time, "rabbitmq");
            for (var i = 0; i < 3; i++)
            {
                entity.PublishFailed("simulated rabbitmq delivery failure", time, 3, TimeSpan.FromSeconds(1));
            }
            await db.Set<OutboxMessageEntity>().AddAsync(entity);
            await db.SaveChangesAsync();
            poisonedId = entity.Id;
        });

        // 1. Build & Start UI Host (Standalone UI: has RabbitMQ & RatatoskrUI, NO local management handler)
        var uiServices = new ServiceCollection();
        uiServices.AddLogging();
        uiServices.AddRatatoskr(bus =>
        {
            bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
        });
        uiServices.AddRatatoskrUI(o =>
        {
            o.UiExchangePrefix = uiPrefix;
            o.RequestTimeout = TimeSpan.FromSeconds(15);
        });

        await using var uiProvider = uiServices.BuildServiceProvider();
        var uiHostedServices = uiProvider.GetServices<IHostedService>().ToList();
        foreach (var svc in uiHostedServices)
        {
            await svc.StartAsync(CancellationToken.None);
        }

        // 2. Build & Start Orders Microservice Host (Has RabbitMQ, EF Core durability, & RatatoskrManagement)
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        serviceCollection.AddDbContext<TestDbContext>(opts => opts.UseNpgsql(PostgresConnectionString));
        serviceCollection.AddRatatoskr(bus =>
        {
            bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
            bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox());
        });
        serviceCollection.AddRatatoskrManagement(o =>
        {
            o.ServiceName = serviceName;
            o.InstanceId = "orders-node-1";
            o.UiExchangePrefix = uiPrefix;
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            o.EnableHeartbeat = true;
        });

        await using var serviceProvider = serviceCollection.BuildServiceProvider();
        var serviceHostedServices = serviceProvider.GetServices<IHostedService>().ToList();
        foreach (var svc in serviceHostedServices)
        {
            await svc.StartAsync(CancellationToken.None);
        }

        try
        {
            var uiClient = uiProvider.GetRequiredService<IRatatoskrBrokerManagementClient>();

            // 3. Verify Heartbeat discovery over RabbitMQ
            ServiceDetailDto? discoveredService = null;
            await WaitForConditionAsync(async () =>
            {
                discoveredService = uiClient.Registry.GetService(serviceName);
                return await Task.FromResult(discoveredService?.Status == "online");
            }, timeout: TimeSpan.FromSeconds(15));

            discoveredService.Should().NotBeNull();
            discoveredService!.ServiceName.Should().Be(serviceName);
            discoveredService.Status.Should().Be("online");
            discoveredService.Instances.Should().HaveCount(1);
            discoveredService.Instances[0].InstanceId.Should().Be("orders-node-1");
            discoveredService.DbContexts.Should().ContainSingle(d => d.DbContextName == "TestDbContext");

            var dbSummary = discoveredService.DbContexts.Single(d => d.DbContextName == "TestDbContext");
            dbSummary.PoisonedOutboxCount.Should().Be(1);

            // 4. Execute RPC over RabbitMQ: GetOutbox
            var outboxResult = await uiClient.ExecuteAsync<GetOutboxMessagesRequest, PagedResult<OutboxItemDto>>(
                serviceName,
                "TestDbContext",
                "GetOutbox",
                new GetOutboxMessagesRequest(Status: "Poisoned", Page: 1, PageSize: 10)
            );

            outboxResult.Should().NotBeNull();
            outboxResult!.TotalCount.Should().Be(1);
            outboxResult.Items.Should().HaveCount(1);
            outboxResult.Items[0].Id.Should().Be(poisonedId);
            outboxResult.Items[0].IsPoisoned.Should().BeTrue();
            outboxResult.Items[0].Error.Should().Contain("simulated rabbitmq delivery failure");

            // 5. Execute RPC over RabbitMQ: GetOutboxDetail (inspect CloudEvents & JSON payload)
            var detailResult = await uiClient.ExecuteAsync<GetOutboxDetailRequest, OutboxDetailDto>(
                serviceName,
                "TestDbContext",
                "GetOutboxDetail",
                new GetOutboxDetailRequest(poisonedId)
            );

            detailResult.Should().NotBeNull();
            detailResult!.Id.Should().Be(poisonedId);
            detailResult.Content.Should().Contain("99.95");
            detailResult.Properties.Should().NotBeNull();
            detailResult.Properties!.Type.Should().Be("orders.order-created");

            // 6. Execute RPC over RabbitMQ: RequeueOutbox
            var requeueResult = await uiClient.ExecuteAsync<RequeueOutboxRequest, RequeueResultDto>(
                serviceName,
                "TestDbContext",
                "RequeueOutbox",
                new RequeueOutboxRequest(poisonedId)
            );

            requeueResult.Should().NotBeNull();
            requeueResult!.RequeuedCount.Should().Be(1);

            // Verify in microservice's database that row was un-poisoned
            await InScopeDatabaseAsync(async db =>
            {
                var row = await db.Set<OutboxMessageEntity>().FindAsync(poisonedId);
                row.Should().NotBeNull();
                row!.IsPoisoned.Should().BeFalse();
                row.ErrorCount.Should().Be(0);
                row.RequeuedCount.Should().Be(1);
            });

            // 7. Verify via RPC that poisoned list is now empty
            var outboxAfterRequeue = await uiClient.ExecuteAsync<GetOutboxMessagesRequest, PagedResult<OutboxItemDto>>(
                serviceName,
                "TestDbContext",
                "GetOutbox",
                new GetOutboxMessagesRequest(Status: "Poisoned", Page: 1, PageSize: 10)
            );

            outboxAfterRequeue.Should().NotBeNull();
            outboxAfterRequeue!.TotalCount.Should().Be(0);
        }
        finally
        {
            foreach (var svc in serviceHostedServices)
            {
                await svc.StopAsync(CancellationToken.None);
            }

            foreach (var svc in uiHostedServices)
            {
                await svc.StopAsync(CancellationToken.None);
            }
        }
    }

    private async Task CreateDatabaseAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(PostgresFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"test_{TestId}\"";
        await command.ExecuteNonQueryAsync();
    }

    private async Task EnsureTestDatabaseSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        await using var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    private async Task InScopeDatabaseAsync(Func<TestDbContext, Task> action)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        await using var db = new TestDbContext(options);
        await action(db);
    }
}
