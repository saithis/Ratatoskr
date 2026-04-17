using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.EfCore.Management;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class OutboxManagementTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : ManagementTestBase(rabbitMq, postgres)
{
    private const string BaseUrl = "/ratatoskr/api/v1/contexts/TestDbContext/outbox";

    [Test]
    public async Task OutboxManagement_PoisonedList_ReturnsPaginatedResults()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();
        await SeedPoisonedOutboxAsync();

        var response = await HttpClient.GetAsync($"{BaseUrl}/poisoned");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalCount").GetInt64().Should().BeGreaterThanOrEqualTo(2);
        body.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task OutboxManagement_PoisonedList_OnlyReturnsPoisonedMessages()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();

        // Add a non-poisoned message
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var props = new MessageProperties { Type = "normal.event" };
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { });
            var entity = OutboxMessageEntity.Create(content, props, time, "efcore");
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
        });

        var response = await HttpClient.GetAsync($"{BaseUrl}/poisoned");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().AllSatisfy(item =>
            item.GetProperty("dbContext").GetString().Should().Be("TestDbContext"));
    }

    [Test]
    public async Task OutboxManagement_PoisonedList_FilterByDateRange()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();

        var time = Services.GetRequiredService<TimeProvider>();
        var future = Uri.EscapeDataString(time.GetUtcNow().AddDays(1).ToString("O"));
        var response = await HttpClient.GetAsync($"{BaseUrl}/poisoned?to={future}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalCount").GetInt64().Should().BeGreaterThan(0);
    }

    [Test]
    public async Task OutboxManagement_Detail_IncludesJsonPayloadAndProperties()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync("detail.event");

        var response = await HttpClient.GetAsync($"{BaseUrl}/poisoned/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(id);
        body.GetProperty("messageType").GetString().Should().Be("detail.event");
        body.TryGetProperty("jsonPayload", out _).Should().BeTrue();
        body.TryGetProperty("payloadBase64", out _).Should().BeTrue();
        body.TryGetProperty("properties", out _).Should().BeTrue();
    }

    [Test]
    public async Task OutboxManagement_Requeue_ClearsIsPoisonedAndResetsCounters()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        var response = await HttpClient.PostAsync($"{BaseUrl}/poisoned/{id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<OutboxMessageEntity>().FindAsync(id);
            updated!.IsPoisoned.Should().BeFalse();
            updated.ErrorCount.Should().Be(0);
        });
    }

    [Test]
    public async Task OutboxManagement_Requeue_IncrementsRequeuedCount()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        await HttpClient.PostAsync($"{BaseUrl}/poisoned/{id}/requeue", null);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<OutboxMessageEntity>().FindAsync(id);
            updated!.RequeuedCount.Should().Be(1);
        });
    }

    [Test]
    public async Task OutboxManagement_Requeue_ClearsProcessingStartedAt()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        await HttpClient.PostAsync($"{BaseUrl}/poisoned/{id}/requeue", null);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<OutboxMessageEntity>().FindAsync(id);
            updated!.ProcessingStartedAt.Should().BeNull();
        });
    }

    [Test]
    public async Task OutboxManagement_Requeue_Returns400ForNonPoisonedMessage()
    {
        await StartManagementTestAsync();

        // Create a normal (non-poisoned) message
        var id = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var props = new MessageProperties { Type = "normal.event" };
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { });
            var entity = OutboxMessageEntity.Create(content, props, time, "efcore");
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });

        var response = await HttpClient.PostAsync($"{BaseUrl}/poisoned/{id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task OutboxManagement_Delete_RemovesMessage()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        var response = await HttpClient.DeleteAsync($"{BaseUrl}/poisoned/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var deleted = await db.Set<OutboxMessageEntity>().FindAsync(id);
            deleted.Should().BeNull();
        });
    }

    [Test]
    public async Task OutboxManagement_BulkRequeue_RequeuesAllSpecifiedIds()
    {
        await StartManagementTestAsync();
        var id1 = await SeedPoisonedOutboxAsync();
        var id2 = await SeedPoisonedOutboxAsync();

        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/poisoned/requeue")
        {
            Content = JsonContent.Create(new BulkRequeueOutboxEndpoint.BulkRequeueOutboxRequest([id1, id2]))
        };
        var response = await HttpClient.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<BulkRequeueOutboxEndpoint.BulkRequeueOutboxResponse>();
        body!.Succeeded.Should().BeEquivalentTo([id1, id2], "both ids must be reported as succeeded");
        body.Failed.Should().BeEmpty("no ids should have failed");

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var count = await db.Set<OutboxMessageEntity>()
                .CountAsync(x => (x.Id == id1 || x.Id == id2) && x.IsPoisoned);
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task OutboxManagement_BulkRequeue_All_RequeuesAllPoisoned()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();
        await SeedPoisonedOutboxAsync();

        var response = await HttpClient.PostAsync($"{BaseUrl}/poisoned/requeue/all", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var poisonedCount = await db.Set<OutboxMessageEntity>().CountAsync(x => x.IsPoisoned);
            poisonedCount.Should().Be(0);
        });
    }

    [Test]
    public async Task OutboxManagement_BulkDelete_DeletesAllSpecifiedIds()
    {
        await StartManagementTestAsync();
        var id1 = await SeedPoisonedOutboxAsync();
        var id2 = await SeedPoisonedOutboxAsync();

        var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/poisoned")
        {
            Content = JsonContent.Create(new BulkDeleteOutboxEndpoint.BulkDeleteOutboxRequest([id1, id2]))
        };
        var response = await HttpClient.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var count = await db.Set<OutboxMessageEntity>()
                .CountAsync(x => x.Id == id1 || x.Id == id2);
            count.Should().Be(0);
        });
    }

    [Test]
    public async Task OutboxManagement_PoisonedList_FilterByType_ExcludesNonMatchingMessages()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync("order.created");
        await SeedPoisonedOutboxAsync("payment.processed");

        var response = await HttpClient.GetAsync($"{BaseUrl}/poisoned?type=order.created");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        items.Should().AllSatisfy(item =>
            item.GetProperty("messageType").GetString().Should().Be("order.created"));

        // TotalCount must reflect the filtered result
        body.GetProperty("totalCount").GetInt64().Should().Be(items.Count);
    }

    [Test]
    public async Task OutboxManagement_BulkRequeue_SpecificIds_SingleRoundtrip_RequeuesAll()
    {
        await StartManagementTestAsync();
        var id1 = await SeedPoisonedOutboxAsync();
        var id2 = await SeedPoisonedOutboxAsync();
        var id3 = await SeedPoisonedOutboxAsync();

        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/poisoned/requeue")
        {
            Content = JsonContent.Create(new BulkRequeueOutboxEndpoint.BulkRequeueOutboxRequest([id1, id2, id3]))
        };
        var response = await HttpClient.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var poisonedCount = await db.Set<OutboxMessageEntity>()
                .CountAsync(x => (x.Id == id1 || x.Id == id2 || x.Id == id3) && x.IsPoisoned);
            poisonedCount.Should().Be(0);

            var requeuedCount = await db.Set<OutboxMessageEntity>()
                .CountAsync(x => (x.Id == id1 || x.Id == id2 || x.Id == id3) && x.RequeuedCount == 1);
            requeuedCount.Should().Be(3);
        });
    }
}
