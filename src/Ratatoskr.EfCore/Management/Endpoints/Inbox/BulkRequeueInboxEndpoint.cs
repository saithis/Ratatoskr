using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class BulkRequeueInboxEndpoint
{
    private const int BatchSize = 500;

    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapPost("/poisoned/requeue", HandleByIdsAsync);
        inboxGroup.MapPost("/poisoned/requeue/all", HandleAllAsync);
    }

    private static async Task<
        Results<Ok<BulkRequeueInboxResponse>, ProblemHttpResult>
    > HandleByIdsAsync(
        string contextName,
        BulkRequeueInboxRequest req,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
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
        var failed = new List<BulkRequeueInboxFailure>();

        var entities = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => req.Ids!.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(ct);

        var foundIds = entities.Select(e => e.Id).ToHashSet();
        failed.AddRange(
            req.Ids!.Where(id => !foundIds.Contains(id))
                .Select(id => new BulkRequeueInboxFailure(
                    id,
                    "Not found, not poisoned, or concurrent modification."
                ))
        );

        foreach (var entity in entities)
        {
            entity.Requeue();
        }

        await SaveBatchAsync(db, entities, succeeded, failed, ct);

        return TypedResults.Ok(new BulkRequeueInboxResponse(succeeded, failed));
    }

    private static async Task<
        Results<Ok<BulkRequeueInboxResponse>, ProblemHttpResult>
    > HandleAllAsync(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        var succeeded = new List<Guid>();
        var failed = new List<BulkRequeueInboxFailure>();

        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Set<InboxHandlerStatusEntity>()
                .Where(x => x.IsPoisoned)
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entity in batch)
            {
                entity.Requeue();
            }

            await SaveBatchAsync(db, batch, succeeded, failed, ct);
            db.ChangeTracker.Clear();
        }

        return TypedResults.Ok(new BulkRequeueInboxResponse(succeeded, failed));
    }

    private static async Task SaveBatchAsync(
        DbContext db,
        List<InboxHandlerStatusEntity> entities,
        List<Guid> succeeded,
        List<BulkRequeueInboxFailure> failed,
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
            failed.AddRange(
                entities.Select(e => new BulkRequeueInboxFailure(
                    e.Id,
                    "Concurrent modification; no rows in this batch were persisted. Retry the failed ids."
                ))
            );
        }
    }

    internal record BulkRequeueInboxRequest(List<Guid>? Ids);

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record BulkRequeueInboxFailure(Guid Id, string Reason);

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record BulkRequeueInboxResponse(
        List<Guid> Succeeded,
        List<BulkRequeueInboxFailure> Failed
    );
}
