using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Bulk operations are bounded by construction: a filter is mandatory, a cap stops the run, and
/// the answer says what is left. These tests pin each of those.
/// </summary>
public class ManagementBulkOperationTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task RequeueMatching_WithAnEmptyFilter_IsRefused()
    {
        // "Apply to absolutely everything" must not be reachable by omitting a field.
        await StartManagementTestAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/delete-matching",
            new MutateMatchingRequest { Filter = new MessageFilter { Status = MessageStatusFilter.All } }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ManagementErrorCodes.FilterRequired);
    }

    [Test]
    public async Task RequeueMatching_OnANonPoisonedStatus_IsRefused()
    {
        // Requeue clears error counters. Doing that to rows a processor is still working on would
        // reset state underneath it, and the predicate would never narrow, so the run could not end.
        await StartManagementTestAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/requeue-matching",
            new MutateMatchingRequest
            {
                Filter = new MessageFilter { Status = MessageStatusFilter.Pending },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RequeueMatching_RequeuesEverythingTheFilterMatches()
    {
        await StartManagementTestAsync();
        for (var index = 0; index < 5; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        await SeedOutboxAsync("healthy.event", poisoned: false);

        var result = await RunMatchingAsync(
            $"{OutboxUrl}/requeue-matching",
            new MessageFilter { Status = MessageStatusFilter.Poisoned }
        );

        result.Processed.Should().Be(5);
        result.Remaining.Should().Be(0);
        result.Capped.Should().BeFalse();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<OutboxMessageEntity>().CountAsync(x => x.IsPoisoned)).Should().Be(0);
            (await db.Set<OutboxMessageEntity>().CountAsync()).Should().Be(6, "nothing was deleted");
        });
    }

    [Test]
    public async Task RequeueMatching_StopsAtTheCapAndSaysWhatIsLeft()
    {
        // The operator asked for a bulk action, not for a single click that rewrites a table. The
        // answer has to say plainly that there is more, or an incident looks resolved when it is not.
        await StartManagementTestAsync(services =>
            services.Configure<ManagementAgentOptions>(options => options.MaxTotalOperations = 2)
        );

        for (var index = 0; index < 5; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        var result = await RunMatchingAsync(
            $"{OutboxUrl}/requeue-matching",
            new MessageFilter { Status = MessageStatusFilter.Poisoned }
        );

        result.Processed.Should().Be(2);
        result.Remaining.Should().Be(3);
        result.Capped.Should().BeTrue();
    }

    [Test]
    public async Task RequeueMatching_HonoursALowerCapFromTheRequest()
    {
        await StartManagementTestAsync();
        for (var index = 0; index < 4; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        var result = await RunMatchingAsync(
            $"{OutboxUrl}/requeue-matching",
            new MessageFilter { Status = MessageStatusFilter.Poisoned },
            maxTotalOperations: 1
        );

        result.Processed.Should().Be(1);
        result.Remaining.Should().Be(3);
        result.Capped.Should().BeTrue();
    }

    [Test]
    public async Task RequeueMatching_CannotBeRaisedAboveTheServerCap()
    {
        await StartManagementTestAsync(services =>
            services.Configure<ManagementAgentOptions>(options => options.MaxTotalOperations = 2)
        );

        for (var index = 0; index < 5; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        var result = await RunMatchingAsync(
            $"{OutboxUrl}/requeue-matching",
            new MessageFilter { Status = MessageStatusFilter.Poisoned },
            maxTotalOperations: 1_000_000
        );

        result.Processed.Should().Be(2, "the server's cap is a ceiling, not a default");
    }

    [Test]
    public async Task RequeueMatching_ReRunAfterBeingCapped_PicksUpWhereItLeftOff()
    {
        // This is the documented recovery: the filter narrows itself, so the same filter under a
        // fresh operation id continues rather than redoing the work.
        await StartManagementTestAsync(services =>
            services.Configure<ManagementAgentOptions>(options => options.MaxTotalOperations = 2)
        );

        for (var index = 0; index < 5; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        var filter = new MessageFilter { Status = MessageStatusFilter.Poisoned };
        var first = await RunMatchingAsync($"{OutboxUrl}/requeue-matching", filter);
        var second = await RunMatchingAsync($"{OutboxUrl}/requeue-matching", filter);
        var third = await RunMatchingAsync($"{OutboxUrl}/requeue-matching", filter);

        first.Processed.Should().Be(2);
        second.Processed.Should().Be(2);
        third.Processed.Should().Be(1);
        third.Remaining.Should().Be(0);
        third.Capped.Should().BeFalse();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<OutboxMessageEntity>().CountAsync(x => x.RequeuedCount != 1))
                .Should()
                .Be(0, "every row was requeued exactly once across the three runs");
        });
    }

    [Test]
    public async Task DeleteMatching_RemovesEverythingTheFilterMatches()
    {
        await StartManagementTestAsync();
        for (var index = 0; index < 3; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        var survivor = await SeedOutboxAsync("healthy.event", poisoned: false);

        var result = await RunMatchingAsync(
            $"{OutboxUrl}/delete-matching",
            new MessageFilter { Status = MessageStatusFilter.Poisoned }
        );

        result.Processed.Should().Be(3);
        result.Remaining.Should().Be(0);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var remaining = await db.Set<OutboxMessageEntity>().ToListAsync();
            remaining.Should().ContainSingle(entity => entity.Id == survivor);
        });
    }

    [Test]
    public async Task DeleteMatching_NarrowedBySearch_LeavesTheRestAlone()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync("order.created");
        await SeedPoisonedOutboxAsync("order.created");
        await SeedPoisonedOutboxAsync("payment.processed");

        var result = await RunMatchingAsync(
            $"{OutboxUrl}/delete-matching",
            new MessageFilter { Status = MessageStatusFilter.Poisoned, Search = "order.created" }
        );

        result.Processed.Should().Be(2);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<OutboxMessageEntity>().CountAsync()).Should().Be(1);
        });
    }

    [Test]
    public async Task Count_PreviewsExactlyWhatTheMatchingMutationWillTouch()
    {
        // The preview and the destructive action go through the same filter code, which is the
        // only way "press preview, see 42, confirm" can be trusted.
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync("order.created");
        await SeedPoisonedOutboxAsync("order.created");
        await SeedPoisonedOutboxAsync("payment.processed");

        using var preview = await HttpClient.GetAsync($"{OutboxUrl}/count?search=order.created");
        var previewed = (await preview.Content.ReadFromJsonAsync<MessageCountResponse>(WebJson))!.Count;

        var applied = await RunMatchingAsync(
            $"{OutboxUrl}/delete-matching",
            new MessageFilter { Status = MessageStatusFilter.Poisoned, Search = "order.created" }
        );

        applied.Processed.Should().Be(previewed);
    }

    private async Task<MatchingMutationResponse> RunMatchingAsync(
        string url,
        MessageFilter filter,
        int? maxTotalOperations = null
    )
    {
        using var response = await HttpClient.PostAsJsonAsync(
            url,
            new MutateMatchingRequest { Filter = filter, MaxTotalOperations = maxTotalOperations }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MatchingMutationResponse>(WebJson))!;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
