using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class InboxManagementTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Inbox_List_ReturnsPoisonedHandlersWithTheirMessageType()
    {
        await StartManagementTestAsync();
        await SeedPoisonedInboxAsync("order.placed");
        await SeedPoisonedInboxAsync("payment.captured");

        var page = await GetPageAsync(InboxUrl);

        page.Items.Should().HaveCount(2);
        page.Items.Should()
            .AllSatisfy(item =>
            {
                item.IsPoisoned.Should().BeTrue();
                item.TransportName.Should().Be("efcore");
            });
        page.Items.Select(item => item.MessageType)
            .Should()
            .BeEquivalentTo(["order.placed", "payment.captured"]);
    }

    [Test]
    public async Task Inbox_Count_ReturnsTheFilteredTotal()
    {
        await StartManagementTestAsync();
        await SeedPoisonedInboxAsync();
        await SeedPoisonedInboxAsync();

        using var response = await HttpClient.GetAsync($"{InboxUrl}/count");

        (await response.Content.ReadFromJsonAsync<MessageCountResponse>())!.Count.Should().Be(2);
    }

    [Test]
    public async Task Inbox_List_SearchNarrowsByMessageType()
    {
        await StartManagementTestAsync();
        await SeedPoisonedInboxAsync("order.placed");
        await SeedPoisonedInboxAsync("payment.captured");

        var page = await GetPageAsync($"{InboxUrl}?search=order.placed");

        page.Items.Should().ContainSingle();
        page.Items[0].MessageType.Should().Be("order.placed");
    }

    [Test]
    public async Task Inbox_Get_ReturnsThePayloadAndSiblingHandlers()
    {
        await StartManagementTestAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync("detail.event");
        var siblingId = await SeedAdditionalPoisonedHandlerAsync(messageId, "handler-b");

        using var response = await HttpClient.GetAsync($"{InboxUrl}/{handlerStatusId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<InboxDetail>(WebJson);
        detail!.HandlerStatusId.Should().Be(handlerStatusId);
        detail.MessageId.Should().Be(messageId);
        detail.MessageType.Should().Be("detail.event");
        detail.HandlerKey.Should().Be("handler-a");
        detail.JsonPayload.Should().Contain("inbox-payload");

        // Knowing which sibling handlers also failed is what tells an operator whether to requeue
        // one handler or the whole message.
        detail.OtherHandlers.Should().ContainSingle(other => other.HandlerStatusId == siblingId);
    }

    [Test]
    public async Task Inbox_Requeue_ClearsPoisonAndResetsCounters()
    {
        await StartManagementTestAsync();
        var (_, handlerStatusId) = await SeedPoisonedInboxAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{InboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [handlerStatusId] }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<InboxHandlerStatusEntity>().FindAsync(handlerStatusId);
            updated!.IsPoisoned.Should().BeFalse();
            updated.ErrorCount.Should().Be(0);
            updated.ProcessingStartedAt.Should().BeNull();
            updated.RequeuedCount.Should().Be(1);
        });
    }

    [Test]
    public async Task Inbox_RequeueMessage_TouchesOnlyThePoisonedHandlers()
    {
        await StartManagementTestAsync();
        var (messageId, _) = await SeedPoisonedInboxAsync();
        await SeedCompletedHandlerAsync(messageId, "handler-b");

        using var response = await HttpClient.PostAsync(
            $"{InboxUrl}/messages/{messageId}/requeue",
            content: null
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var stillPoisoned = await db.Set<InboxHandlerStatusEntity>()
                .CountAsync(x => x.MessageId == messageId && x.IsPoisoned);
            stillPoisoned.Should().Be(0);

            // A handler that already succeeded must not be re-run: the operator asked to retry a
            // failure, not to deliver the message twice.
            var completed = await db.Set<InboxHandlerStatusEntity>()
                .CountAsync(x => x.MessageId == messageId && x.CompletedAt != null);
            completed.Should().Be(1);
        });
    }

    [Test]
    public async Task Inbox_RequeueMessage_ReturnsNotFoundWhenNothingIsPoisoned()
    {
        await StartManagementTestAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync();

        await HttpClient.PostAsJsonAsync(
            $"{InboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [handlerStatusId] }
        );

        using var response = await HttpClient.PostAsync(
            $"{InboxUrl}/messages/{messageId}/requeue",
            content: null
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Inbox_Delete_RemovesTheMessageOnceItsLastHandlerIsGone()
    {
        // Leaving the parent behind keeps the deduplication anchor alive, so a redelivery of that
        // message would be discarded as a duplicate and never reach a handler again.
        await StartManagementTestAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{InboxUrl}/delete",
            new MutateByIdsRequest { Ids = [handlerStatusId] }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<InboxHandlerStatusEntity>().FindAsync(handlerStatusId)).Should().BeNull();
            (await db.Set<InboxMessageEntity>().FindAsync(messageId)).Should().BeNull();
        });
    }

    [Test]
    public async Task Inbox_Delete_KeepsTheMessageWhileOtherHandlersReferenceIt()
    {
        await StartManagementTestAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync();
        var survivorId = await SeedCompletedHandlerAsync(messageId, "handler-b");

        using var response = await HttpClient.PostAsJsonAsync(
            $"{InboxUrl}/delete",
            new MutateByIdsRequest { Ids = [handlerStatusId] }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<InboxHandlerStatusEntity>().FindAsync(survivorId)).Should().NotBeNull();
            (await db.Set<InboxMessageEntity>().FindAsync(messageId)).Should().NotBeNull();
        });
    }

    [Test]
    public async Task Inbox_DeleteMatching_RemovesOrphanedMessagesToo()
    {
        await StartManagementTestAsync();
        var (firstMessage, _) = await SeedPoisonedInboxAsync();
        var (secondMessage, _) = await SeedPoisonedInboxAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{InboxUrl}/delete-matching",
            new MutateMatchingRequest { Filter = new MessageFilter { Status = MessageStatusFilter.Poisoned } }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<MatchingMutationResponse>(WebJson);
        result!.Processed.Should().Be(2);
        result.Remaining.Should().Be(0);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<InboxHandlerStatusEntity>().CountAsync()).Should().Be(0);
            (await db.Set<InboxMessageEntity>().FindAsync(firstMessage)).Should().BeNull();
            (await db.Set<InboxMessageEntity>().FindAsync(secondMessage)).Should().BeNull();
        });
    }

    private async Task<Guid> SeedCompletedHandlerAsync(string messageId, string handlerKey)
    {
        var id = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var handler = InboxHandlerStatusEntity.Create(messageId, handlerKey, time);
            handler.MarkAsCompleted(time);
            db.Set<InboxHandlerStatusEntity>().Add(handler);
            await db.SaveChangesAsync();
            id = handler.Id;
        });
        return id;
    }

    private async Task<CursorPage<InboxListItem>> GetPageAsync(string url)
    {
        using var response = await HttpClient.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CursorPage<InboxListItem>>(WebJson))!;
    }
}
