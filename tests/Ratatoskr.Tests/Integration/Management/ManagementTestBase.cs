using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Base class for management API integration tests.
/// Sets up an HTTP client, authorization, and a seeded test database.
/// </summary>
public abstract class ManagementTestBase(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    protected HttpClient HttpClient { get; private set; } = null!;

    protected async Task StartManagementTestAsync(Action<IServiceCollection>? configure = null)
    {
        await StartTestAsync(services =>
        {
            // "Always allow" policy — no real auth needed in tests
            services.AddAuthorization(o =>
                o.AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true)));

            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox());
            });

            services.AddDbContext<TestDbContext>((_, opts) =>
                opts.UseNpgsql(PostgresConnectionString));

            configure?.Invoke(services);
        });

        await InitializeDatabase();
        HttpClient = CreateHttpClient();
    }

    /// <summary>Seeds a poisoned outbox message and returns its ID.</summary>
    protected async Task<Guid> SeedPoisonedOutboxAsync(string? messageType = null)
    {
        var id = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var props = new MessageProperties { Type = messageType ?? "test.event", Id = Guid.NewGuid().ToString() };
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Data = "payload" });
            var entity = OutboxMessageEntity.Create(content, props, TimeProvider.System, "efcore");
            // Poison it by exceeding max retries
            for (var i = 0; i < 4; i++)
                entity.PublishFailed("simulated error", TimeProvider.System, 3, TimeSpan.FromSeconds(1));
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });
        return id;
    }

    /// <summary>Seeds a poisoned inbox handler status and returns (messageId, handlerStatusId).</summary>
    protected async Task<(string MessageId, Guid HandlerStatusId)> SeedPoisonedInboxAsync(
        string? messageType = null)
    {
        string messageId = null!;
        Guid handlerStatusId = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var props = new MessageProperties { Type = messageType ?? "test.event" };
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Data = "inbox-payload" });
            var msg = InboxMessageEntity.Create(
                Guid.NewGuid().ToString(), "efcore", content, props, TimeProvider.System);
            db.Set<InboxMessageEntity>().Add(msg);

            var handler = InboxHandlerStatusEntity.Create(msg.Id, "handler-a", TimeProvider.System);
            for (var i = 0; i < 4; i++)
                handler.MarkAsFailed("simulated inbox error", TimeProvider.System, 3, TimeSpan.FromSeconds(1));
            db.Set<InboxHandlerStatusEntity>().Add(handler);

            await db.SaveChangesAsync();
            messageId = msg.Id;
            handlerStatusId = handler.Id;
        });
        return (messageId, handlerStatusId);
    }
}
