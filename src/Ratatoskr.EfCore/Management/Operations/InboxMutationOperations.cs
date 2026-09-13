using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Idempotency;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>Clears the poisoned state on the named inbox handler rows.</summary>
internal sealed class RequeueInboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog
) : IdListMutationOperation(resolver, operationLog)
{
    public override string Name => ManagementOperationNames.InboxRequeue;

    protected override DurabilityFeature Feature => DurabilityFeature.Inbox;

    protected override async Task<IdListMutationPlan> PlanAsync(
        DbContext db,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken
    )
    {
        var entities = await db.Set<InboxHandlerStatusEntity>()
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

/// <summary>
/// Removes the named poisoned inbox handler rows, and the parent message once no handler
/// references it any more.
/// </summary>
/// <remarks>
/// Leaving the parent behind would keep the deduplication anchor alive, so a redelivery of that
/// message id would be silently discarded as a duplicate and never reach a handler again. Deleting
/// the last handler row therefore has to delete the message with it.
/// </remarks>
internal sealed class DeleteInboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog
) : IdListMutationOperation(resolver, operationLog)
{
    public override string Name => ManagementOperationNames.InboxDelete;

    protected override DurabilityFeature Feature => DurabilityFeature.Inbox;

    protected override async Task<IdListMutationPlan> PlanAsync(
        DbContext db,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken
    )
    {
        var entities = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => ids.Contains(x.Id) && x.IsPoisoned)
            .ToListAsync(cancellationToken);

        var removedIds = entities.Select(entity => entity.Id).ToHashSet();
        var touchedMessages = entities
            .Select(entity => entity.MessageId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        db.Set<InboxHandlerStatusEntity>().RemoveRange(entities);

        if (touchedMessages.Length > 0)
        {
            // Whether a message is orphaned is decided against the rows that survive this delete,
            // so the check runs over the same change-tracked state that is about to be committed.
            var survivorsByMessage = await db.Set<InboxHandlerStatusEntity>()
                .AsNoTracking()
                .Where(x => touchedMessages.Contains(x.MessageId))
                .Select(x => new { x.Id, x.MessageId })
                .ToListAsync(cancellationToken);

            var orphaned = touchedMessages
                .Where(messageId =>
                    !survivorsByMessage.Exists(row =>
                        row.MessageId == messageId && !removedIds.Contains(row.Id)
                    )
                )
                .ToArray();

            if (orphaned.Length > 0)
            {
                var messages = await db.Set<InboxMessageEntity>()
                    .Where(message => orphaned.Contains(message.Id))
                    .ToListAsync(cancellationToken);
                db.Set<InboxMessageEntity>().RemoveRange(messages);
            }
        }

        return new IdListMutationPlan(
            [.. removedIds],
            [
                .. ids.Where(id => !removedIds.Contains(id))
                    .Select(id => new MutationFailure(
                        id,
                        ManagementErrorCodes.NotFound,
                        "Not found or not poisoned."
                    )),
            ]
        );
    }
}

/// <summary>
/// Requeues every poisoned handler of one message. The common recovery after a bad deploy: the
/// message was fine, the handlers were not.
/// </summary>
internal sealed class RequeueInboxMessageOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog
) : IManagementOperation
{
    public string Name => ManagementOperationNames.InboxRequeueMessage;

    public Type RequestType => typeof(RequeueInboxMessageRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<RequeueInboxMessageRequest>();

        if (resolver.Resolve(context, DurabilityFeature.Inbox, out var db) is { } failure)
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(request.MessageId))
        {
            return ManagementResult.Invalid("A message id is required.");
        }

        var fingerprint = request.MessageId;
        var lookup = await operationLog.LookupAsync(
            db,
            context.OperationId,
            fingerprint,
            cancellationToken
        );

        switch (lookup.Disposition)
        {
            case ManagementOperationDisposition.AlreadyCompleted:
                return ManagementOperationLog.Replay<MutationResponse>(lookup.Record!);
            case ManagementOperationDisposition.FilterMismatch:
                return ManagementOperationLog.FilterMismatch(context.OperationId);
        }

        var handlers = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.MessageId == request.MessageId && x.IsPoisoned)
            .ToListAsync(cancellationToken);

        if (handlers.Count == 0)
        {
            return ManagementResult.NotFound(
                $"No poisoned handlers were found for inbox message '{request.MessageId}'."
            );
        }

        foreach (var handler in handlers)
        {
            handler.Requeue();
        }

        var response = new MutationResponse([.. handlers.Select(handler => handler.Id)], []);

        if (lookup.Record is { } resumed)
        {
            resumed.ProcessedCount = handlers.Count;
            operationLog.Complete(resumed, response);
        }
        else
        {
            operationLog.StageCompleted(db, context, response, handlers.Count, fingerprint);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ManagementResult.Conflict(
                "One or more handlers were modified concurrently, so nothing was applied. Retry with the same operation id."
            );
        }

        return ManagementResult.Ok(response);
    }
}
