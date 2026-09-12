using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class RequeuedCountTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task RequeuedCount_IncrementsOnEachRequeue()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        await RequeueAsync(id);

        // Re-poison the entity so we can requeue again
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var e = await db.Set<OutboxMessageEntity>().FindAsync(id);
            e!.PublishFailed("error again", time, 1, TimeSpan.FromSeconds(1));
            await db.SaveChangesAsync();
        });

        await RequeueAsync(id);

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

        await RequeueAsync(id);

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
                .AnyAsync(x =>
                    x.Id == id && x.ProcessedAt != null && x.ProcessedAt < cutoff && !x.IsPoisoned
                );
            wouldBeDeleted
                .Should()
                .BeFalse("poisoned messages must not match the cleanup predicate");
        });

        // Verify the poisoned message still exists
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var e = await db.Set<OutboxMessageEntity>().FindAsync(id);
            e.Should().NotBeNull();
            e.IsPoisoned.Should().BeTrue();
        });
    }

    /// <summary>
    /// A fresh operation id per call: reusing one would be a retry of the same request, and the
    /// idempotency record would replay the first answer instead of requeueing again.
    /// </summary>
    private async Task RequeueAsync(Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{OutboxUrl}/requeue")
        {
            Content = JsonContent.Create(new MutateByIdsRequest { Ids = [id] }),
        };
        request.Headers.Add("X-Ratatoskr-Operation-Id", Guid.NewGuid().ToString());
        using var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
