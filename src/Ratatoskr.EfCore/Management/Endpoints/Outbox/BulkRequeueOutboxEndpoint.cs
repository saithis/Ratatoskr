using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static class BulkRequeueOutboxEndpoint
{
    private const int BatchSize = 500;

    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        outboxGroup.MapPost("/poisoned/requeue", HandleByIdsAsync);
        outboxGroup.MapPost("/poisoned/requeue/all", HandleAllAsync);
    }

    private static async Task<
        Results<Ok<BulkRequeueOutboxResponse>, ProblemHttpResult>
    > HandleByIdsAsync(
        string contextName,
        BulkRequeueOutboxRequest req,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureOutbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        if (!BulkRequestValidator.TryValidateIds(req.Ids, out var error))
        {
            return ManagementResults.BadRequest(error!);
        }

        var succeeded = new List<Guid>();
        var failed = new List<BulkRequeueOutboxFailure>();

        var distinctIds = req.Ids!.Distinct().ToList();

        var entities = await db.Set<OutboxMessageEntity>()
            .Where(x => distinctIds.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(ct);

        var foundIds = entities.Select(e => e.Id).ToHashSet();
        failed.AddRange(
            distinctIds
                .Where(id => !foundIds.Contains(id))
                .Select(id => new BulkRequeueOutboxFailure(
                    id,
                    "Not found, not poisoned, or concurrent modification."
                ))
        );

        foreach (var entity in entities)
        {
            entity.Requeue();
        }

        await SaveBatchAsync(db, entities, succeeded, failed, ct);

        return TypedResults.Ok(new BulkRequeueOutboxResponse(succeeded, failed));
    }

    private static async Task<
        Results<Ok<BulkRequeueOutboxResponse>, ProblemHttpResult>
    > HandleAllAsync(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureOutbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        var succeeded = new List<Guid>();
        var failed = new List<BulkRequeueOutboxFailure>();

        // Process in batches so "requeue all" with tens of thousands of poisoned messages
        // does not build a single massive transaction. Each batch reuses the domain
        // Requeue() method so invariants (Version increment, counter reset, etc.)
        // stay in sync with the rest of the processor.
        var consecutiveNoProgress = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Set<OutboxMessageEntity>()
                .Where(x => x.IsPoisoned)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var succeededBefore = succeeded.Count;
            foreach (var entity in batch)
            {
                entity.Requeue();
            }

            await SaveBatchAsync(db, batch, succeeded, failed, ct);
            db.ChangeTracker.Clear();

            if (succeeded.Count == succeededBefore)
            {
                if (++consecutiveNoProgress >= 3)
                {
                    break; // give up; return what we have
                }
            }
            else
            {
                consecutiveNoProgress = 0;
            }
        }

        return TypedResults.Ok(new BulkRequeueOutboxResponse(succeeded, failed));
    }

    private static async Task SaveBatchAsync(
        DbContext db,
        List<OutboxMessageEntity> entities,
        List<Guid> succeeded,
        List<BulkRequeueOutboxFailure> failed,
        CancellationToken ct
    )
    {
        try
        {
            await db.SaveChangesAsync(ct);
            succeeded.AddRange(entities.Select(e => e.Id));
        }
        catch (DbUpdateConcurrencyException)
        {
            // SaveChanges wraps the batch in a transaction that is rolled back on any
            // concurrency conflict, so no row in this batch was persisted. Report all
            // of them as failed rather than partially claiming success for rows that
            // were actually rolled back.
            failed.AddRange(
                entities.Select(e => new BulkRequeueOutboxFailure(
                    e.Id,
                    "Concurrent modification; no rows in this batch were persisted. Retry the failed ids."
                ))
            );
        }
    }

    internal record BulkRequeueOutboxRequest(List<Guid>? Ids);

    internal record BulkRequeueOutboxFailure(Guid Id, string Reason);

    internal record BulkRequeueOutboxResponse(
        List<Guid> Succeeded,
        List<BulkRequeueOutboxFailure> Failed
    );
}
