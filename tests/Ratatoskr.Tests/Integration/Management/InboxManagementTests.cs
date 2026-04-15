using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class InboxManagementTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : ManagementTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task InboxManagement_PoisonedList_ReturnsPaginatedResults()
    {
        await StartManagementTestAsync();
        await SeedPoisonedInboxAsync();
        await SeedPoisonedInboxAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/inbox/poisoned");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalCount").GetInt64().Should().BeGreaterThanOrEqualTo(2);
        body.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task InboxManagement_RequeueHandler_ClearsIsPoisonedAndResetsCounters()
    {
        await StartManagementTestAsync();
        var (_, handlerStatusId) = await SeedPoisonedInboxAsync();

        var response = await HttpClient.PostAsync(
            $"/ratatoskr/api/v1/inbox/poisoned/{handlerStatusId}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<InboxHandlerStatusEntity>().FindAsync(handlerStatusId);
            updated!.IsPoisoned.Should().BeFalse();
            updated.ErrorCount.Should().Be(0);
            updated.ProcessingStartedAt.Should().BeNull();
        });
    }

    [Test]
    public async Task InboxManagement_RequeueAllHandlersForMessage_RequeuesOnlyPoisoned()
    {
        await StartManagementTestAsync();
        var (messageId, _) = await SeedPoisonedInboxAsync();

        // Add a completed handler for the same message
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var completed = InboxHandlerStatusEntity.Create(messageId, "handler-b", TimeProvider.System);
            completed.MarkAsCompleted(TimeProvider.System);
            db.Set<InboxHandlerStatusEntity>().Add(completed);
            await db.SaveChangesAsync();
        });

        var response = await HttpClient.PostAsync(
            $"/ratatoskr/api/v1/inbox/messages/{messageId}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var poisoned = await db.Set<InboxHandlerStatusEntity>()
                .CountAsync(x => x.MessageId == messageId && x.IsPoisoned);
            poisoned.Should().Be(0);
            // Completed handler should not be touched
            var completed = await db.Set<InboxHandlerStatusEntity>()
                .CountAsync(x => x.MessageId == messageId && x.CompletedAt != null);
            completed.Should().Be(1);
        });
    }

    [Test]
    public async Task InboxManagement_GetHandlersForMessage_ReturnsAllStatuses()
    {
        await StartManagementTestAsync();
        var (messageId, _) = await SeedPoisonedInboxAsync();

        // Add a second completed handler
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var second = InboxHandlerStatusEntity.Create(messageId, "handler-b", TimeProvider.System);
            second.MarkAsCompleted(TimeProvider.System);
            db.Set<InboxHandlerStatusEntity>().Add(second);
            await db.SaveChangesAsync();
        });

        var response = await HttpClient.GetAsync($"/ratatoskr/api/v1/inbox/messages/{messageId}/handlers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("handlers").GetArrayLength().Should().Be(2);
        body.GetProperty("messageId").GetString().Should().Be(messageId);
    }

    [Test]
    public async Task InboxManagement_DeleteHandlerStatus_DeletesOrphanedParentMessage()
    {
        await StartManagementTestAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync();

        // Delete the only handler status
        var response = await HttpClient.DeleteAsync(
            $"/ratatoskr/api/v1/inbox/poisoned/{handlerStatusId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var handlerGone = await db.Set<InboxHandlerStatusEntity>().FindAsync(handlerStatusId);
            handlerGone.Should().BeNull();

            // Parent message should also be gone (orphan cleanup)
            var msgGone = await db.Set<InboxMessageEntity>().FindAsync(messageId);
            msgGone.Should().BeNull();
        });
    }

    [Test]
    public async Task InboxManagement_DeleteHandlerStatus_DoesNotDeleteParentWhenOtherHandlersExist()
    {
        await StartManagementTestAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync();

        // Add a second handler (completed)
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var second = InboxHandlerStatusEntity.Create(messageId, "handler-b", TimeProvider.System);
            second.MarkAsCompleted(TimeProvider.System);
            db.Set<InboxHandlerStatusEntity>().Add(second);
            await db.SaveChangesAsync();
        });

        await HttpClient.DeleteAsync($"/ratatoskr/api/v1/inbox/poisoned/{handlerStatusId}");

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            // Parent message should remain (still has handler-b)
            var msgStillExists = await db.Set<InboxMessageEntity>().FindAsync(messageId);
            msgStillExists.Should().NotBeNull();
        });
    }
}
