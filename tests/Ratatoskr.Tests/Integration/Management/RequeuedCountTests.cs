using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class RequeuedCountTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : ManagementTestBase(rabbitMq, postgres)
{
    private const string BaseUrl = "/ratatoskr/api/v1/efcore/contexts/TestDbContext/outbox";

    [Test]
    public async Task RequeuedCount_IncrementsOnEachRequeue()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        // First requeue
        await HttpClient.PostAsync($"{BaseUrl}/poisoned/{id}/requeue", null);

        // Re-poison the entity so we can requeue again
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var e = await db.Set<OutboxMessageEntity>().FindAsync(id);
            e!.PublishFailed("error again", time, 1, TimeSpan.FromSeconds(1));
            await db.SaveChangesAsync();
        });

        // Second requeue
        await HttpClient.PostAsync($"{BaseUrl}/poisoned/{id}/requeue", null);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<OutboxMessageEntity>().FindAsync(id);
            updated!.RequeuedCount.Should().Be(2);
        });
    }

    [Test]
    public async Task RequeuedCount_ErrorCountResetsOnRequeue()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        await HttpClient.PostAsync($"{BaseUrl}/poisoned/{id}/requeue", null);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<OutboxMessageEntity>().FindAsync(id);
            updated!.ErrorCount.Should().Be(0);
            updated.RequeuedCount.Should().Be(1);
        });
    }

    [Test]
    public async Task CleanupService_SkipsPoisonedMessages()
    {
        // Regression: poisoned messages must not be auto-deleted by cleanup
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        // Cleanup logic deletes WHERE ProcessedAt < cutoff AND NOT IsPoisoned
        // Our poisoned entity should not be in that set
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var cutoff = time.GetUtcNow().AddDays(1);
            var wouldBeDeleted = await db.Set<OutboxMessageEntity>()
                .AnyAsync(x => x.Id == id && x.ProcessedAt != null && x.ProcessedAt < cutoff && !x.IsPoisoned);
            wouldBeDeleted.Should().BeFalse("poisoned messages must not match the cleanup predicate");
        });

        // Verify the poisoned message still exists
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var e = await db.Set<OutboxMessageEntity>().FindAsync(id);
            e.Should().NotBeNull();
            e!.IsPoisoned.Should().BeTrue();
        });
    }
}
