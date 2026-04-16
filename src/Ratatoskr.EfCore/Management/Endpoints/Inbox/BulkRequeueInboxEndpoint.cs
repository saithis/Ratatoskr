using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class BulkRequeueInboxEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapPost("/poisoned/requeue", Handle);
    }

    private static async Task<Results<Ok<BulkRequeueInboxResponse>, NotFound>> Handle(
        string contextName,
        BulkRequeueInboxRequest req,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var succeeded = new List<Guid>();
        var failed = new List<BulkRequeueInboxFailure>();

        if (req.All is true)
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await db.Set<InboxHandlerStatusEntity>()
                    .Where(x => x.IsPoisoned)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct);

                if (batch.Count == 0) break;

                foreach (var entity in batch) entity.Requeue();
                await SaveBatchAsync(db, batch, succeeded, failed, ct);
                db.ChangeTracker.Clear();
            }

            return TypedResults.Ok(new BulkRequeueInboxResponse(succeeded, failed));
        }

        if (req.Ids is null or { Count: 0 })
            return TypedResults.Ok(new BulkRequeueInboxResponse(succeeded, failed));

        // Fetch all matching poisoned entities in a single query instead of N individual lookups.
        var entities = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => req.Ids.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(ct);

        var foundIds = entities.Select(e => e.Id).ToHashSet();
        failed.AddRange(req.Ids
            .Where(id => !foundIds.Contains(id))
            .Select(id => new BulkRequeueInboxFailure(id, "Not found, not poisoned, or concurrent modification.")));

        foreach (var entity in entities) entity.Requeue();
        await SaveBatchAsync(db, entities, succeeded, failed, ct);

        return TypedResults.Ok(new BulkRequeueInboxResponse(succeeded, failed));
    }

    private const int BatchSize = 500;

    private static async Task SaveBatchAsync(
        Microsoft.EntityFrameworkCore.DbContext db,
        List<InboxHandlerStatusEntity> entities,
        List<Guid> succeeded,
        List<BulkRequeueInboxFailure> failed,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            succeeded.AddRange(entities.Select(e => e.Id));
        }
        catch (DbUpdateConcurrencyException)
        {
            failed.AddRange(entities.Select(e =>
                new BulkRequeueInboxFailure(e.Id, "Concurrent modification; no rows in this batch were persisted. Retry the failed ids.")));
        }
    }

    internal record BulkRequeueInboxRequest(List<Guid>? Ids, bool? All);

    internal record BulkRequeueInboxFailure(Guid Id, string Reason);

    internal record BulkRequeueInboxResponse(List<Guid> Succeeded, List<BulkRequeueInboxFailure> Failed);
}
