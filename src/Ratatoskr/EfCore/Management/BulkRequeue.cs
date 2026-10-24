using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Shared implementation of the "requeue poisoned rows" bulk endpoints for the inbox and the outbox.
/// </summary>
internal static class BulkRequeue
{
    private const int BatchSize = 500;

    /// <summary>"Requeue all" gives up after this many batches in a row that requeued nothing.</summary>
    private const int MaxConsecutiveBatchesWithoutProgress = 3;

    internal static async Task<Results<Ok<BulkRequeueResponse>, ProblemHttpResult>> ByIdsAsync<TEntity>(
        DbContext db,
        BulkRequeueRequest req,
        CancellationToken ct
    )
        where TEntity : class, IRequeueableEntity
    {
        if (!BulkRequestValidator.TryValidateIds(req.Ids, out var error))
        {
            return ManagementResults.BadRequest(error!);
        }

        var response = new BulkRequeueResponse([], []);
        var distinctIds = req.Ids!.Distinct().ToList();

        var entities = await db.Set<TEntity>()
            .Where(x => distinctIds.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(ct);

        var foundIds = entities.Select(e => e.Id).ToHashSet();
        response.Failed.AddRange(
            distinctIds
                .Where(id => !foundIds.Contains(id))
                .Select(id => new BulkRequeueFailure(
                    id,
                    "Not found, not poisoned, or concurrent modification."
                ))
        );

        await RequeueBatchAsync(db, entities, response, ct);

        return TypedResults.Ok(response);
    }

    internal static async Task<Ok<BulkRequeueResponse>> AllAsync<TEntity>(
        DbContext db,
        CancellationToken ct
    )
        where TEntity : class, IRequeueableEntity
    {
        var response = new BulkRequeueResponse([], []);

        // Process in batches so "requeue all" with tens of thousands of poisoned rows
        // does not build a single massive transaction. Each batch reuses the domain
        // Requeue() method so invariants (Version increment, counter reset, etc.)
        // stay in sync with the rest of the processor.
        var consecutiveNoProgress = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Set<TEntity>()
                .Where(x => x.IsPoisoned)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var succeededBefore = response.Succeeded.Count;
            await RequeueBatchAsync(db, batch, response, ct);
            db.ChangeTracker.Clear();

            // A batch that keeps conflicting would otherwise be fetched again forever.
            if (response.Succeeded.Count > succeededBefore)
            {
                consecutiveNoProgress = 0;
            }
            else if (++consecutiveNoProgress >= MaxConsecutiveBatchesWithoutProgress)
            {
                break;
            }
        }

        return TypedResults.Ok(response);
    }

    private static async Task RequeueBatchAsync<TEntity>(
        DbContext db,
        List<TEntity> entities,
        BulkRequeueResponse response,
        CancellationToken ct
    )
        where TEntity : class, IRequeueableEntity
    {
        foreach (var entity in entities)
        {
            entity.Requeue();
        }

        try
        {
            await db.SaveChangesAsync(ct);
            response.Succeeded.AddRange(entities.Select(e => e.Id));
        }
        catch (DbUpdateConcurrencyException)
        {
            // SaveChanges wraps the batch in a transaction that is rolled back on any
            // concurrency conflict, so no row in this batch was persisted. Report all
            // of them as failed rather than partially claiming success for rows that
            // were actually rolled back.
            response.Failed.AddRange(
                entities.Select(e => new BulkRequeueFailure(
                    e.Id,
                    "Concurrent modification; no rows in this batch were persisted. Retry the failed ids."
                ))
            );
        }
    }
}

internal record BulkRequeueRequest(List<Guid>? Ids);

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
internal record BulkRequeueFailure(Guid Id, string Reason);

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
internal record BulkRequeueResponse(List<Guid> Succeeded, List<BulkRequeueFailure> Failed);
