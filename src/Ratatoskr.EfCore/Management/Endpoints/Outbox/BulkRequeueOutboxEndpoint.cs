using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class BulkRequeueOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapPost("/poisoned/requeue", Handle);
    }

    private static async Task<Results<Ok<BulkRequeueOutboxResponse>, NotFound>> Handle(
        string contextName,
        BulkRequeueOutboxRequest req,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        if (req.All is true)
        {
            await db.Set<OutboxMessageEntity>()
                .Where(x => x.IsPoisoned)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsPoisoned, false)
                    .SetProperty(x => x.ErrorCount, 0)
                    .SetProperty(x => x.Error, string.Empty)
                    .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.ProcessingStartedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.RequeuedCount, x => x.RequeuedCount + 1)
                    .SetProperty(x => x.Version, x => x.Version + 1), ct);
            return TypedResults.Ok(new BulkRequeueOutboxResponse([], []));
        }

        if (req.Ids is null or { Count: 0 })
            return TypedResults.Ok(new BulkRequeueOutboxResponse([], []));

        // Fetch all matching poisoned entities in a single query instead of N individual lookups.
        var entities = await db.Set<OutboxMessageEntity>()
            .Where(x => req.Ids.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(ct);

        var foundIds = entities.Select(e => e.Id).ToHashSet();
        var notFoundOrNotPoisoned = req.Ids.Where(id => !foundIds.Contains(id)).ToList();

        foreach (var entity in entities)
            entity.Requeue();

        var succeeded = new List<Guid>();
        var failed = new List<BulkRequeueOutboxFailure>(
            notFoundOrNotPoisoned.Select(id =>
                new BulkRequeueOutboxFailure(id, "Not found, not poisoned, or concurrent modification.")));

        try
        {
            await db.SaveChangesAsync(ct);
            succeeded.AddRange(entities.Select(e => e.Id));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // On optimistic concurrency failure report the conflicting entries as failed.
            var conflictedIds = ex.Entries
                .Select(e => ((OutboxMessageEntity)e.Entity).Id)
                .ToHashSet();
            succeeded.AddRange(entities.Select(e => e.Id).Where(id => !conflictedIds.Contains(id)));
            failed.AddRange(conflictedIds.Select(id =>
                new BulkRequeueOutboxFailure(id, "Not found, not poisoned, or concurrent modification.")));
        }

        return TypedResults.Ok(new BulkRequeueOutboxResponse(succeeded, failed));
    }

    internal record BulkRequeueOutboxRequest(List<Guid>? Ids, bool? All);

    internal record BulkRequeueOutboxFailure(Guid Id, string Reason);

    internal record BulkRequeueOutboxResponse(List<Guid> Succeeded, List<BulkRequeueOutboxFailure> Failed);
}
