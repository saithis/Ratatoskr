using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Idempotency;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>Clears the poisoned state on the named outbox rows so the processor retries them.</summary>
internal sealed class RequeueOutboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog
) : IdListMutationOperation(resolver, operationLog)
{
    public override string Name => ManagementOperationNames.OutboxRequeue;

    protected override DurabilityFeature Feature => DurabilityFeature.Outbox;

    protected override async Task<IdListMutationPlan> PlanAsync(
        DbContext db,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken
    )
    {
        // Only poisoned rows are eligible: requeueing a row the processor is already working on
        // would reset its error counters underneath it.
        var entities = await db.Set<OutboxMessageEntity>()
            .Where(x => ids.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(cancellationToken);

        foreach (var entity in entities)
        {
            entity.Requeue();
        }

        var found = entities.Select(entity => entity.Id).ToHashSet();
        return new IdListMutationPlan(
            [.. found],
            [
                .. ids.Where(id => !found.Contains(id))
                    .Select(id => new MutationFailure(
                        id,
                        ManagementErrorCodes.NotFound,
                        "Not found or not poisoned."
                    )),
            ]
        );
    }
}

/// <summary>Permanently removes the named poisoned outbox rows.</summary>
internal sealed class DeleteOutboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog
) : IdListMutationOperation(resolver, operationLog)
{
    public override string Name => ManagementOperationNames.OutboxDelete;

    protected override DurabilityFeature Feature => DurabilityFeature.Outbox;

    protected override async Task<IdListMutationPlan> PlanAsync(
        DbContext db,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken
    )
    {
        var entities = await db.Set<OutboxMessageEntity>()
            .Where(x => ids.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(cancellationToken);

        db.Set<OutboxMessageEntity>().RemoveRange(entities);

        var found = entities.Select(entity => entity.Id).ToHashSet();
        return new IdListMutationPlan(
            [.. found],
            [
                .. ids.Where(id => !found.Contains(id))
                    .Select(id => new MutationFailure(
                        id,
                        ManagementErrorCodes.NotFound,
                        "Not found or not poisoned."
                    )),
            ]
        );
    }
}
