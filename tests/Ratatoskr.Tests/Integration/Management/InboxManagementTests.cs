using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.EfCore.Management.Endpoints.Inbox;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class InboxManagementTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private const string BaseUrl = "/ratatoskr/api/v1/efcore/contexts/TestDbContext/inbox";

    [Test]
    public async Task InboxManagement_PoisonedList_ReturnsPaginatedResults()
    {
        await StartManagementTestAsync();
        await SeedPoisonedInboxAsync();
        await SeedPoisonedInboxAsync();

        using var response = await HttpClient.GetAsync($"{BaseUrl}/poisoned");
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

        using var response = await HttpClient.PostAsync(
            $"{BaseUrl}/poisoned/{handlerStatusId}/requeue",
            content: null
        );
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
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var completed = InboxHandlerStatusEntity.Create(messageId, "handler-b", time);
            completed.MarkAsCompleted(time);
            await db.Set<InboxHandlerStatusEntity>().AddAsync(completed);
            await db.SaveChangesAsync();
        });

        using var response = await HttpClient.PostAsync(
            $"{BaseUrl}/messages/{messageId}/requeue",
            content: null
        );
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
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var second = InboxHandlerStatusEntity.Create(messageId, "handler-b", time);
            second.MarkAsCompleted(time);
            await db.Set<InboxHandlerStatusEntity>().AddAsync(second);
            await db.SaveChangesAsync();
        });

        using var response = await HttpClient.GetAsync($"{BaseUrl}/messages/{messageId}/handlers");
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
        using var response = await HttpClient.DeleteAsync($"{BaseUrl}/poisoned/{handlerStatusId}");
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
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var second = InboxHandlerStatusEntity.Create(messageId, "handler-b", time);
            second.MarkAsCompleted(time);
            await db.Set<InboxHandlerStatusEntity>().AddAsync(second);
            await db.SaveChangesAsync();
        });

        using var response = await HttpClient.DeleteAsync($"{BaseUrl}/poisoned/{handlerStatusId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            // Parent message should remain (still has handler-b)
            var msgStillExists = await db.Set<InboxMessageEntity>().FindAsync(messageId);
            msgStillExists.Should().NotBeNull();
        });
    }

    [Test]
    public async Task InboxManagement_BulkDelete_All_DeletesOrphanedParentMessages()
    {
        await StartManagementTestAsync();
        var (messageId1, _) = await SeedPoisonedInboxAsync();
        var (messageId2, _) = await SeedPoisonedInboxAsync();

        using var response = await HttpClient.DeleteAsync($"{BaseUrl}/poisoned/all");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            // All handler statuses should be gone
            var handlerCount = await db.Set<InboxHandlerStatusEntity>().CountAsync();
            handlerCount.Should().Be(0);

            // Orphaned parent messages should also be deleted
            var msg1 = await db.Set<InboxMessageEntity>().FindAsync(messageId1);
            msg1.Should().BeNull();

            var msg2 = await db.Set<InboxMessageEntity>().FindAsync(messageId2);
            msg2.Should().BeNull();
        });
    }

    [Test]
    public async Task InboxManagement_BulkDelete_SpecificIds_DeletesOrphanedParentMessages()
    {
        await StartManagementTestAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync();

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/poisoned")
        {
            Content = JsonContent.Create(
                new BulkDeleteInboxEndpoint.BulkDeleteInboxRequest([handlerStatusId])
            ),
        };
        using var response = await HttpClient.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            // Handler status should be gone
            var handler = await db.Set<InboxHandlerStatusEntity>().FindAsync(handlerStatusId);
            handler.Should().BeNull();

            // Orphaned parent message should also be deleted
            var msg = await db.Set<InboxMessageEntity>().FindAsync(messageId);
            msg.Should().BeNull();
        });
    }

    [Test]
    public async Task InboxManagement_BulkDelete_SpecificIds_PreservesParentWithRemainingHandlers()
    {
        await StartManagementTestAsync();
        var (messageId, poisonedHandlerStatusId) = await SeedPoisonedInboxAsync();

        // Add a second (completed) handler for the same message
        var secondHandlerId = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var second = InboxHandlerStatusEntity.Create(messageId, "handler-b", time);
            second.MarkAsCompleted(time);
            await db.Set<InboxHandlerStatusEntity>().AddAsync(second);
            await db.SaveChangesAsync();
            secondHandlerId = second.Id;
        });

        // Delete only the poisoned handler
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/poisoned")
        {
            Content = JsonContent.Create(
                new BulkDeleteInboxEndpoint.BulkDeleteInboxRequest([poisonedHandlerStatusId])
            ),
        };
        await HttpClient.SendAsync(req);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();

            // The completed handler should still exist
            var remaining = await db.Set<InboxHandlerStatusEntity>().FindAsync(secondHandlerId);
            remaining.Should().NotBeNull();

            // Parent message must still exist
            var msg = await db.Set<InboxMessageEntity>().FindAsync(messageId);
            msg.Should().NotBeNull();
        });
    }

    [Test]
    public async Task InboxManagement_PoisonedList_SearchFilter_ExcludesNonMatchingMessages()
    {
        await StartManagementTestAsync();
        await SeedPoisonedInboxAsync("order.placed");
        await SeedPoisonedInboxAsync("payment.captured");

        using var response = await HttpClient.GetAsync($"{BaseUrl}/poisoned?search=order.placed");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").ToElementList();
        items.Should().NotBeEmpty();
        items
            .Should()
            .AllSatisfy(item =>
                item.GetProperty("messageType").GetString().Should().Be("order.placed")
            );

        // TotalCount must reflect the filtered result
        body.GetProperty("totalCount").GetInt64().Should().Be(items.Count);
    }
}
