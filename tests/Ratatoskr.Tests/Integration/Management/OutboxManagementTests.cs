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

public class OutboxManagementTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Outbox_List_ReturnsPoisonedMessagesByDefault()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();
        await SeedPoisonedOutboxAsync();
        await SeedOutboxAsync("healthy.event", poisoned: false);

        var page = await GetPageAsync(OutboxUrl);

        page.Items.Should().HaveCount(2);
        page.Items.Should().AllSatisfy(item => item.IsPoisoned.Should().BeTrue());
    }

    [Test]
    public async Task Outbox_List_CarriesNoTotalCount()
    {
        // A total is a second full scan of the filtered set, which is the most expensive part of
        // the query on a retained table. Callers that need one ask /count explicitly.
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();

        using var response = await HttpClient.GetAsync(OutboxUrl);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("totalCount", out _).Should().BeFalse();
        body.TryGetProperty("items", out _).Should().BeTrue();
    }

    [Test]
    public async Task Outbox_Count_ReturnsTheFilteredTotal()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();
        await SeedPoisonedOutboxAsync();
        await SeedOutboxAsync("healthy.event", poisoned: false);

        using var poisoned = await HttpClient.GetAsync($"{OutboxUrl}/count");
        (await poisoned.Content.ReadFromJsonAsync<MessageCountResponse>())!
            .Count.Should()
            .Be(2);

        using var all = await HttpClient.GetAsync($"{OutboxUrl}/count?status=All");
        (await all.Content.ReadFromJsonAsync<MessageCountResponse>())!.Count.Should().Be(3);
    }

    [Test]
    public async Task Outbox_List_PagesWithAStableKeysetCursor()
    {
        await StartManagementTestAsync();
        var seeded = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            seeded.Add(await SeedPoisonedOutboxAsync());
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = cursor is null
                ? $"{OutboxUrl}?limit=2"
                : $"{OutboxUrl}?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await GetPageAsync(url);
            seen.AddRange(page.Items.Select(item => item.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        seen.Should().BeEquivalentTo(seeded, "every seeded row appears exactly once across pages");
    }

    [Test]
    public async Task Outbox_List_RejectsAMalformedCursor()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync($"{OutboxUrl}?cursor=not-a-real-cursor");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ManagementErrorCodes.InvalidCursor);
    }

    [Test]
    public async Task Outbox_List_FiltersByDateWindow()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();

        var now = Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var future = Uri.EscapeDataString(now.AddDays(1).ToString("O"));
        var farFuture = Uri.EscapeDataString(now.AddDays(2).ToString("O"));

        (await GetPageAsync($"{OutboxUrl}?to={future}")).Items.Should().NotBeEmpty();
        (await GetPageAsync($"{OutboxUrl}?from={future}&to={farFuture}")).Items.Should().BeEmpty();
    }

    [Test]
    public async Task Outbox_List_SearchNarrowsByMessageType()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync("order.created");
        await SeedPoisonedOutboxAsync("payment.processed");

        var page = await GetPageAsync($"{OutboxUrl}?search=order.created");

        page.Items.Should().NotBeEmpty();
        page.Items.Should().AllSatisfy(item => item.MessageType.Should().Be("order.created"));
    }

    [Test]
    public async Task Outbox_List_RejectsAnUnboundedSearch()
    {
        // A leading-wildcard LIKE cannot use an index, so a search across every row of a retained
        // table is refused rather than quietly becoming a full scan.
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync($"{OutboxUrl}?status=All&search=anything");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ManagementErrorCodes.UnboundedSearch);
    }

    [Test]
    public async Task Outbox_Get_ReturnsThePayloadAndProperties()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync("detail.event");

        using var response = await HttpClient.GetAsync($"{OutboxUrl}/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<OutboxDetail>(WebJson);
        detail!.Id.Should().Be(id);
        detail.MessageType.Should().Be("detail.event");
        detail.JsonPayload.Should().Contain("payload");
        detail.PayloadBase64.Should().NotBeEmpty();
        detail.Properties.Type.Should().Be("detail.event");
    }

    [Test]
    public async Task Outbox_Get_ReturnsNotFoundForAnUnknownId()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync($"{OutboxUrl}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Outbox_Requeue_ClearsPoisonAndResetsCounters()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        var result = await RequeueAsync([id]);

        result.Succeeded.Should().BeEquivalentTo([id]);
        result.Failed.Should().BeEmpty();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var updated = await db.Set<OutboxMessageEntity>().FindAsync(id);
            updated!.IsPoisoned.Should().BeFalse();
            updated.ErrorCount.Should().Be(0);
            updated.ProcessingStartedAt.Should().BeNull();
            updated.RequeuedCount.Should().Be(1);
        });
    }

    [Test]
    public async Task Outbox_Requeue_ReportsIdsThatWereNotPoisoned()
    {
        await StartManagementTestAsync();
        var poisoned = await SeedPoisonedOutboxAsync();
        var healthy = await SeedOutboxAsync("healthy.event", poisoned: false);

        var result = await RequeueAsync([poisoned, healthy]);

        result.Succeeded.Should().BeEquivalentTo([poisoned]);
        result.Failed.Should().ContainSingle(failure => failure.Id == healthy);
    }

    [Test]
    public async Task Outbox_Requeue_RejectsAnEmptyIdList()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest()
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ManagementErrorCodes.FilterRequired);
    }

    [Test]
    public async Task Outbox_Requeue_RejectsMoreIdsThanTheCap()
    {
        await StartManagementTestAsync();
        var ids = Enumerable
            .Range(0, ManagementPaging.MaxMutationIds + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = ids }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Outbox_Delete_RemovesThePoisonedRows()
    {
        await StartManagementTestAsync();
        var first = await SeedPoisonedOutboxAsync();
        var second = await SeedPoisonedOutboxAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/delete",
            new MutateByIdsRequest { Ids = [first, second] }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var remaining = await db.Set<OutboxMessageEntity>()
                .CountAsync(x => x.Id == first || x.Id == second);
            remaining.Should().Be(0);
        });
    }

    [Test]
    public async Task Outbox_UnknownContext_ReturnsNotFound()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/api/v1/contexts/NoSuchDbContext/outbox"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private async Task<CursorPage<OutboxListItem>> GetPageAsync(string url)
    {
        using var response = await HttpClient.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CursorPage<OutboxListItem>>(WebJson))!;
    }

    private async Task<MutationResponse> RequeueAsync(IReadOnlyList<Guid> ids)
    {
        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = ids }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MutationResponse>(WebJson))!;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
