using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Idempotency;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>Requeues every poisoned outbox row a filter matches.</summary>
internal sealed class RequeueMatchingOutboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
) : MatchingMutationOperation(resolver, operationLog, options, timeProvider)
{
    public override string Name => ManagementOperationNames.OutboxRequeueMatching;

    protected override DurabilityFeature Feature => DurabilityFeature.Outbox;

    protected override bool PoisonedOnly => true;

    protected override Task<long> CountAsync(
        DbContext db,
        MessageFilter filter,
        CancellationToken cancellationToken
    ) => MessageQueries.Outbox(db, filter).LongCountAsync(cancellationToken);

    protected override async Task<BatchOutcome> ApplyBatchAsync(
        DbContext db,
        MessageFilter filter,
        ManagementCursor? cursor,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        var batch = await MessageQueries
            .After(MessageQueries.Outbox(db, filter), cursor)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .AsTracking()
            .ToListAsync(cancellationToken);

        foreach (var entity in batch)
        {
            entity.Requeue();
        }

        return new BatchOutcome(
            batch.Count,
            batch.Count == 0 ? null : new ManagementCursor(batch[^1].CreatedAt, batch[^1].Id)
        );
    }
}

/// <summary>Deletes every outbox row a filter matches.</summary>
internal sealed class DeleteMatchingOutboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
) : MatchingMutationOperation(resolver, operationLog, options, timeProvider)
{
    public override string Name => ManagementOperationNames.OutboxDeleteMatching;

    protected override DurabilityFeature Feature => DurabilityFeature.Outbox;

    protected override bool PoisonedOnly => false;

    protected override Task<long> CountAsync(
        DbContext db,
        MessageFilter filter,
        CancellationToken cancellationToken
    ) => MessageQueries.Outbox(db, filter).LongCountAsync(cancellationToken);

    protected override async Task<BatchOutcome> ApplyBatchAsync(
        DbContext db,
        MessageFilter filter,
        ManagementCursor? cursor,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        var batch = await MessageQueries
            .After(MessageQueries.Outbox(db, filter), cursor)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .AsTracking()
            .ToListAsync(cancellationToken);

        db.Set<OutboxMessageEntity>().RemoveRange(batch);

        // Deleted rows cannot reappear, so the next batch restarts at the top of the filter
        // rather than carrying a cursor past rows that no longer exist.
        return new BatchOutcome(batch.Count, NextCursor: null);
    }
}

/// <summary>Requeues every poisoned inbox handler row a filter matches.</summary>
internal sealed class RequeueMatchingInboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
) : MatchingMutationOperation(resolver, operationLog, options, timeProvider)
{
    public override string Name => ManagementOperationNames.InboxRequeueMatching;

    protected override DurabilityFeature Feature => DurabilityFeature.Inbox;

    protected override bool PoisonedOnly => true;

    protected override Task<long> CountAsync(
        DbContext db,
        MessageFilter filter,
        CancellationToken cancellationToken
    ) => MessageQueries.Inbox(db, filter).LongCountAsync(cancellationToken);

    protected override async Task<BatchOutcome> ApplyBatchAsync(
        DbContext db,
        MessageFilter filter,
        ManagementCursor? cursor,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        var batch = await MessageQueries
            .After(MessageQueries.Inbox(db, filter), cursor)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .AsTracking()
            .ToListAsync(cancellationToken);

        foreach (var status in batch)
        {
            status.Requeue();
        }

        return new BatchOutcome(
            batch.Count,
            batch.Count == 0 ? null : new ManagementCursor(batch[^1].CreatedAt, batch[^1].Id)
        );
    }
}

/// <summary>
/// Deletes every inbox handler row a filter matches, and any message left without handlers.
/// </summary>
internal sealed class DeleteMatchingInboxOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
) : MatchingMutationOperation(resolver, operationLog, options, timeProvider)
{
    public override string Name => ManagementOperationNames.InboxDeleteMatching;

    protected override DurabilityFeature Feature => DurabilityFeature.Inbox;

    protected override bool PoisonedOnly => false;

    protected override Task<long> CountAsync(
        DbContext db,
        MessageFilter filter,
        CancellationToken cancellationToken
    ) => MessageQueries.Inbox(db, filter).LongCountAsync(cancellationToken);

    protected override async Task<BatchOutcome> ApplyBatchAsync(
        DbContext db,
        MessageFilter filter,
        ManagementCursor? cursor,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        var batch = await MessageQueries
            .After(MessageQueries.Inbox(db, filter), cursor)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .AsTracking()
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return new BatchOutcome(0, NextCursor: null);
        }

        var removedIds = batch.Select(status => status.Id).ToHashSet();
        var touchedMessages = batch.Select(status => status.MessageId).Distinct().ToArray();
        db.Set<InboxHandlerStatusEntity>().RemoveRange(batch);

        // A message whose last handler row goes with this batch has to go too: leaving it behind
        // keeps the deduplication anchor alive, and a redelivery would then be discarded as a
        // duplicate and never reach a handler again.
        var survivors = await db.Set<InboxHandlerStatusEntity>()
            .AsNoTracking()
            .Where(x => touchedMessages.Contains(x.MessageId))
            .Select(x => new { x.Id, x.MessageId })
            .ToListAsync(cancellationToken);

        var orphaned = touchedMessages
            .Where(messageId =>
                !survivors.Any(row => row.MessageId == messageId && !removedIds.Contains(row.Id))
            )
            .ToArray();

        if (orphaned.Length > 0)
        {
            var messages = await db.Set<InboxMessageEntity>()
                .Where(message => orphaned.Contains(message.Id))
                .ToListAsync(cancellationToken);
            db.Set<InboxMessageEntity>().RemoveRange(messages);
        }

        return new BatchOutcome(batch.Count, NextCursor: null);
    }
}
