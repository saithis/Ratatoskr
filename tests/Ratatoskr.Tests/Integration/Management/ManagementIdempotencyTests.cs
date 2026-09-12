using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Http;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Control-plane delivery is at least once, so the same mutation arrives twice whenever a
/// response is lost or an agent restarts mid-flight. These tests pin the behaviour that makes
/// that safe.
/// </summary>
public class ManagementIdempotencyTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task Requeue_ReplayedWithTheSameOperationId_MutatesOnceAndAnswersTheSame()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();
        var operationId = Guid.NewGuid();

        using var first = await PostAsync($"{OutboxUrl}/requeue", new MutateByIdsRequest { Ids = [id] }, operationId);
        using var second = await PostAsync($"{OutboxUrl}/requeue", new MutateByIdsRequest { Ids = [id] }, operationId);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstBody = await first.Content.ReadFromJsonAsync<MutationResponse>(WebJson);
        var secondBody = await second.Content.ReadFromJsonAsync<MutationResponse>(WebJson);
        secondBody.Should().BeEquivalentTo(firstBody, "the replay returns the recorded result");

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().FindAsync(id);
            entity!.RequeuedCount.Should().Be(1, "the second delivery replayed rather than requeueing again");
        });
    }

    [Test]
    public async Task Delete_ReplayedWithTheSameOperationId_ReportsTheOriginalSuccess()
    {
        // The row is gone by the time the duplicate arrives, so without the recorded result the
        // replay would report "not found" for a delete that actually succeeded.
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();
        var operationId = Guid.NewGuid();

        using var first = await PostAsync($"{OutboxUrl}/delete", new MutateByIdsRequest { Ids = [id] }, operationId);
        using var second = await PostAsync($"{OutboxUrl}/delete", new MutateByIdsRequest { Ids = [id] }, operationId);

        var replay = await second.Content.ReadFromJsonAsync<MutationResponse>(WebJson);
        replay!.Succeeded.Should().BeEquivalentTo([id]);
        replay.Failed.Should().BeEmpty();
    }

    [Test]
    public async Task Requeue_ReplayedWithADifferentIdList_IsRefused()
    {
        // An operation id is the caller's promise that this is the same request. Honouring it for
        // a different id list would apply a mutation the caller never asked for.
        await StartManagementTestAsync();
        var first = await SeedPoisonedOutboxAsync();
        var second = await SeedPoisonedOutboxAsync();
        var operationId = Guid.NewGuid();

        using var original = await PostAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [first] },
            operationId
        );
        original.StatusCode.Should().Be(HttpStatusCode.OK);

        using var reused = await PostAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [second] },
            operationId
        );

        reused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var untouched = await db.Set<OutboxMessageEntity>().FindAsync(second);
            untouched!.IsPoisoned.Should().BeTrue("the mismatched replay applied nothing");
        });
    }

    [Test]
    public async Task MatchingRequeue_ReplayedWithTheSameFilter_ReportsTheRecordedTotal()
    {
        await StartManagementTestAsync();
        for (var index = 0; index < 3; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        var operationId = Guid.NewGuid();
        var request = new MutateMatchingRequest
        {
            Filter = new MessageFilter { Status = MessageStatusFilter.Poisoned },
        };

        using var first = await PostAsync($"{OutboxUrl}/requeue-matching", request, operationId);
        using var second = await PostAsync($"{OutboxUrl}/requeue-matching", request, operationId);

        var firstBody = await first.Content.ReadFromJsonAsync<MatchingMutationResponse>(WebJson);
        var secondBody = await second.Content.ReadFromJsonAsync<MatchingMutationResponse>(WebJson);

        firstBody!.Processed.Should().Be(3);
        secondBody.Should().BeEquivalentTo(firstBody, "the replay returns the recorded result");

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var requeuedTwice = await db.Set<OutboxMessageEntity>().CountAsync(x => x.RequeuedCount > 1);
            requeuedTwice.Should().Be(0);
        });
    }

    [Test]
    public async Task MatchingRequeue_InterruptedMidRun_ResumesAndReportsAnAccurateTotal()
    {
        // Simulates a crash between batches: the record exists and says how far the run got, so a
        // redelivery continues into the same record instead of restarting the count at zero.
        await StartManagementTestAsync();
        for (var index = 0; index < 4; index++)
        {
            await SeedPoisonedOutboxAsync();
        }

        var operationId = Guid.NewGuid();

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();

            // One row was already requeued by the interrupted run, and its progress was recorded.
            var first = await db.Set<OutboxMessageEntity>().OrderBy(x => x.CreatedAt).FirstAsync();
            first.Requeue();

            db.Set<ManagementOperationEntity>()
                .Add(
                    new ManagementOperationEntity
                    {
                        OperationId = operationId,
                        CreatedAt = time.GetUtcNow(),
                        Operation = ManagementOperationNames.OutboxRequeueMatching,
                        State = ManagementOperationState.InProgress,
                        FilterJson = JsonSerializer.Serialize(
                            new MessageFilter { Status = MessageStatusFilter.Poisoned },
                            ManagementJson.Options
                        ),
                        ProcessedCount = 1,
                    }
                );

            await db.SaveChangesAsync();
        });

        using var response = await PostAsync(
            $"{OutboxUrl}/requeue-matching",
            new MutateMatchingRequest
            {
                Filter = new MessageFilter { Status = MessageStatusFilter.Poisoned },
            },
            operationId
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MatchingMutationResponse>(WebJson);

        body!.Processed.Should().Be(4, "one row from the interrupted run plus the three that remained");
        body.Remaining.Should().Be(0);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            (await db.Set<OutboxMessageEntity>().CountAsync(x => x.IsPoisoned)).Should().Be(0);
            (await db.Set<OutboxMessageEntity>().CountAsync(x => x.RequeuedCount > 1))
                .Should()
                .Be(0, "the row the interrupted run already requeued was not requeued twice");
        });
    }

    [Test]
    public async Task MatchingRequeue_ReplayedWithADifferentFilter_IsRefused()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();
        var operationId = Guid.NewGuid();

        using var first = await PostAsync(
            $"{OutboxUrl}/requeue-matching",
            new MutateMatchingRequest
            {
                Filter = new MessageFilter { Status = MessageStatusFilter.Poisoned },
            },
            operationId
        );
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var reused = await PostAsync(
            $"{OutboxUrl}/requeue-matching",
            new MutateMatchingRequest
            {
                Filter = new MessageFilter
                {
                    Status = MessageStatusFilter.Poisoned,
                    Search = "something-else",
                    From = DateTimeOffset.UnixEpoch,
                },
            },
            operationId
        );

        reused.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Mutation_RecordsTheActorThatAskedForIt()
    {
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();
        var operationId = Guid.NewGuid();

        using var response = await PostAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [id] },
            operationId
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var record = await db.Set<ManagementOperationEntity>().FindAsync(operationId);
            record.Should().NotBeNull();
            record!.Actor.Should().Be("operator-1");
            record.Operation.Should().Be(ManagementOperationNames.OutboxRequeue);
            record.State.Should().Be(ManagementOperationState.Completed);
        });
    }

    /// <summary>
    /// Posts with an explicit operation id, which is how a caller says "this is a retry of the
    /// same request" rather than "do it again".
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync<TRequest>(
        string url,
        TRequest payload,
        Guid operationId
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add(ManagementApiRoutes.OperationIdHeader, operationId.ToString());
        return await HttpClient.SendAsync(request);
    }
}
