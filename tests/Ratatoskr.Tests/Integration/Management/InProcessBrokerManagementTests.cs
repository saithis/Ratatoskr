using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;
using Ratatoskr.UI.Client;
using TUnit.Core;

namespace Ratatoskr.Tests.Integration.Management;

public class InProcessBrokerManagementTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string SecondPostgresConnectionString
    {
        get
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(PostgresFixture.ConnectionString)
            {
                Database = $"test_{TestId}_second",
                MaxPoolSize = 2,
            };
            return builder.ToString();
        }
    }

    public override async Task StartTestAsync(Action<IServiceCollection>? configure = null)
    {
        await CreateSecondDatabaseAsync();
        await base.StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox());
                bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseInbox().UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (_, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddDbContext<SecondTestDbContext>(
                (_, opts) => opts.UseNpgsql(SecondPostgresConnectionString)
            );

            services.AddRatatoskrManagement(options =>
            {
                options.ServiceName = "monolith";
                options.InstanceId = "mono-1";
                options.EnableHeartbeat = false; // in-process mode
            });

            services.AddRatatoskrUI();

            configure?.Invoke(services);
        });

        await InitializeBothDatabasesAsync();
    }

    private async Task CreateSecondDatabaseAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(PostgresFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"test_{TestId}_second\"";
        await command.ExecuteNonQueryAsync();
    }

    private async Task InitializeBothDatabasesAsync()
    {
        await InScopeAsync(async ctx =>
        {
            var db1 = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await db1.Database.EnsureCreatedAsync();

            var db2 = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            await db2.Database.EnsureCreatedAsync();
        });
    }

    [Test]
    public async Task InProcess_MultiDbContext_AggregatesStatsAndHandlesRequests()
    {
        await StartTestAsync();

        // Seed 1 poisoned outbox in TestDbContext
        var outboxId = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var props = new MessageProperties
            {
                Id = Guid.NewGuid().ToString(),
                Type = "test.outbox.event",
                Source = "test-service",
            };
            var content = JsonSerializer.SerializeToUtf8Bytes(new { Message = "Hello Outbox" });
            var entity = OutboxMessageEntity.Create(content, props, time, "efcore");
            for (var i = 0; i < 3; i++)
            {
                entity.PublishFailed("simulated failure", time, 3, TimeSpan.FromSeconds(1));
            }
            await db.Set<OutboxMessageEntity>().AddAsync(entity);
            await db.SaveChangesAsync();
            outboxId = entity.Id;
        });

        // Seed 1 poisoned inbox in SecondTestDbContext
        var statusId = Guid.Empty;
        var inboxMsgId = Guid.NewGuid().ToString();
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var props = new MessageProperties
            {
                Id = inboxMsgId,
                Type = "test.inbox.event",
                Source = "test-service",
            };
            var content = JsonSerializer.SerializeToUtf8Bytes(new { Message = "Hello Inbox" });
            var msg = InboxMessageEntity.Create(inboxMsgId, "efcore", content, props, time);
            await db.Set<InboxMessageEntity>().AddAsync(msg);

            var handler = InboxHandlerStatusEntity.Create(msg.Id, "second-handler", time);
            for (var i = 0; i < 3; i++)
            {
                handler.MarkAsFailed("inbox processing error", time, 3, TimeSpan.FromSeconds(1));
            }
            await db.Set<InboxHandlerStatusEntity>().AddAsync(handler);
            await db.SaveChangesAsync();
            statusId = handler.Id;
        });

        var client = Services.GetRequiredService<IRatatoskrBrokerManagementClient>();

        // 1. GetStats
        var stats = await client.ExecuteAsync<object, ServiceHeartbeat>(
            "monolith",
            contextName: null,
            "GetStats",
            new { }
        );

        stats.Should().NotBeNull();
        stats!.ServiceName.Should().Be("monolith");
        stats.DbContexts.Should().HaveCount(2);

        var testDbSummary = stats.DbContexts.Single(d => d.DbContextName == "TestDbContext");
        testDbSummary.PoisonedOutboxCount.Should().Be(1);
        testDbSummary.PoisonedInboxCount.Should().Be(0);

        var secondDbSummary = stats.DbContexts.Single(d => d.DbContextName == "SecondTestDbContext");
        secondDbSummary.PoisonedOutboxCount.Should().Be(0);
        secondDbSummary.PoisonedInboxCount.Should().Be(1);

        // 2. Query Outbox List & Detail
        var outboxList = await client.ExecuteAsync<GetOutboxMessagesRequest, PagedResult<OutboxItemDto>>(
            "monolith",
            "TestDbContext",
            "GetOutbox",
            new GetOutboxMessagesRequest(Status: "Poisoned", Page: 1, PageSize: 10)
        );

        outboxList.Should().NotBeNull();
        outboxList!.TotalCount.Should().Be(1);
        outboxList.Items.Should().HaveCount(1);
        outboxList.Items[0].Id.Should().Be(outboxId);
        outboxList.Items[0].IsPoisoned.Should().BeTrue();
        outboxList.Items[0].Error.Should().Contain("simulated failure");

        var outboxDetail = await client.ExecuteAsync<GetOutboxDetailRequest, OutboxDetailDto>(
            "monolith",
            "TestDbContext",
            "GetOutboxDetail",
            new GetOutboxDetailRequest(outboxId)
        );

        outboxDetail.Should().NotBeNull();
        outboxDetail!.Id.Should().Be(outboxId);
        outboxDetail.Content.Should().Contain("Hello Outbox");
        outboxDetail.Properties.Should().NotBeNull();
        outboxDetail.Properties!.Type.Should().Be("test.outbox.event");

        // 3. Requeue Outbox
        var requeueRes = await client.ExecuteAsync<RequeueOutboxRequest, RequeueResultDto>(
            "monolith",
            "TestDbContext",
            "RequeueOutbox",
            new RequeueOutboxRequest(outboxId)
        );

        requeueRes.Should().NotBeNull();
        requeueRes!.RequeuedCount.Should().Be(1);

        // Verify entity in DB is no longer poisoned
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().FindAsync(outboxId);
            entity.Should().NotBeNull();
            entity!.IsPoisoned.Should().BeFalse();
            entity.ErrorCount.Should().Be(0);
            entity.RequeuedCount.Should().Be(1);
        });

        // 4. Query Inbox List & Detail
        var inboxList = await client.ExecuteAsync<GetInboxMessagesRequest, PagedResult<InboxItemDto>>(
            "monolith",
            "SecondTestDbContext",
            "GetInbox",
            new GetInboxMessagesRequest(Status: "Poisoned", Page: 1, PageSize: 10)
        );

        inboxList.Should().NotBeNull();
        inboxList!.TotalCount.Should().Be(1);
        inboxList.Items.Should().HaveCount(1);
        inboxList.Items[0].Id.Should().Be(statusId);
        inboxList.Items[0].HandlerKey.Should().Be("second-handler");
        inboxList.Items[0].IsPoisoned.Should().BeTrue();

        var inboxDetail = await client.ExecuteAsync<GetInboxDetailRequest, InboxDetailDto>(
            "monolith",
            "SecondTestDbContext",
            "GetInboxDetail",
            new GetInboxDetailRequest(statusId)
        );

        inboxDetail.Should().NotBeNull();
        inboxDetail!.Id.Should().Be(statusId);
        inboxDetail.Content.Should().Contain("Hello Inbox");
        inboxDetail.Properties.Should().NotBeNull();
        inboxDetail.Properties!.Type.Should().Be("test.inbox.event");

        // 5. Requeue Inbox Handler
        var inboxRequeueRes = await client.ExecuteAsync<RequeueInboxHandlerRequest, RequeueResultDto>(
            "monolith",
            "SecondTestDbContext",
            "RequeueInboxHandler",
            new RequeueInboxHandlerRequest(statusId)
        );

        inboxRequeueRes.Should().NotBeNull();
        inboxRequeueRes!.RequeuedCount.Should().Be(1);

        // Verify handler in DB is no longer poisoned
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var handler = await db.Set<InboxHandlerStatusEntity>().FindAsync(statusId);
            handler.Should().NotBeNull();
            handler!.IsPoisoned.Should().BeFalse();
            handler.ErrorCount.Should().Be(0);
            handler.RequeuedCount.Should().Be(1);
        });

        // 6. Delete Outbox and Inbox
        var delOutbox = await client.ExecuteAsync<DeleteOutboxRequest, DeleteResultDto>(
            "monolith",
            "TestDbContext",
            "DeleteOutbox",
            new DeleteOutboxRequest(outboxId)
        );
        delOutbox.Should().NotBeNull();
        delOutbox!.DeletedCount.Should().Be(1);

        var delInbox = await client.ExecuteAsync<DeleteInboxHandlerRequest, DeleteResultDto>(
            "monolith",
            "SecondTestDbContext",
            "DeleteInboxHandler",
            new DeleteInboxHandlerRequest(statusId)
        );
        delInbox.Should().NotBeNull();
        delInbox!.DeletedCount.Should().Be(1);

        // Verify deletion in DB
        await InScopeAsync(async ctx =>
        {
            var db1 = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var o = await db1.Set<OutboxMessageEntity>().FindAsync(outboxId);
            o.Should().BeNull();

            var db2 = ctx.ServiceProvider.GetRequiredService<SecondTestDbContext>();
            var h = await db2.Set<InboxHandlerStatusEntity>().FindAsync(statusId);
            h.Should().BeNull();
        });
    }
}
