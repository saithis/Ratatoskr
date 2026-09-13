using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Idempotency;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>
/// The shared shape of "mutate everything this filter matches": bounded, resumable, and honest
/// about what it did not get to.
/// </summary>
/// <remarks>
/// There is deliberately no unbounded variant. A filter is mandatory, the run stops at
/// <see cref="ManagementAgentOptions.MaxTotalOperations"/> or at the request deadline, and the
/// answer reports <c>{processed, remaining, capped}</c> so an operator knows to run it again
/// rather than assuming an incident is over.
/// <para>
/// Progress is committed in batches together with a running count, which is the only arrangement
/// that survives a crash mid-run: writing the record completed up front would mark a
/// partially-applied operation as done, and writing it only at the end would leave an interrupted
/// run with no record at all.
/// </para>
/// </remarks>
internal abstract class MatchingMutationOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
) : IManagementOperation
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public Type RequestType => typeof(MutateMatchingRequest);

    /// <summary>Which half of the durability model this mutation needs.</summary>
    protected abstract DurabilityFeature Feature { get; }

    /// <summary>
    /// Whether this mutation only makes sense against poisoned rows. Requeueing does: it clears
    /// the poisoned flag, which is what makes the predicate narrow itself and a resumed run pick
    /// up only what is left. Deleting narrows by construction.
    /// </summary>
    protected abstract bool PoisonedOnly { get; }

    /// <summary>Counts the rows still matching <paramref name="filter"/>.</summary>
    protected abstract Task<long> CountAsync(
        DbContext db,
        MessageFilter filter,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Applies one batch, starting after <paramref name="cursor"/>, and returns how many rows it
    /// touched plus the key to continue from. Does not save; the caller commits the batch and the
    /// progress counter together.
    /// </summary>
    protected abstract Task<BatchOutcome> ApplyBatchAsync(
        DbContext db,
        MessageFilter filter,
        ManagementCursor? cursor,
        int batchSize,
        CancellationToken cancellationToken
    );

    /// <inheritdoc />
    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<MutateMatchingRequest>();

        if (resolver.Resolve(context, Feature, out var db) is { } failure)
        {
            return failure;
        }

        if (MessageQueries.Validate(request.Filter, requireNonEmpty: true) is { } invalid)
        {
            return invalid;
        }

        if (PoisonedOnly && request.Filter.Status is not MessageStatusFilter.Poisoned)
        {
            return ManagementResult.Invalid(
                $"'{Name}' applies only to poisoned rows: requeueing anything else would reset the "
                    + "error counters of rows the processor is still working on. Set status to 'poisoned'."
            );
        }

        var fingerprint = ManagementOperationLog.Serialize(request.Filter);
        var lookup = await operationLog.LookupAsync(
            db,
            context.OperationId,
            fingerprint,
            cancellationToken
        );

        if (lookup.Disposition is ManagementOperationDisposition.AlreadyCompleted)
        {
            return ManagementOperationLog.Replay<MatchingMutationResponse>(lookup.Record!);
        }

        if (lookup.Disposition is ManagementOperationDisposition.FilterMismatch)
        {
            return ManagementOperationLog.FilterMismatch(context.OperationId);
        }

        if (lookup.Record is null)
        {
            operationLog.StageInProgress(db, context, fingerprint);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Another delivery of the same command got there first; let it own the run.
                db.ChangeTracker.Clear();
                var winner = await operationLog.LookupAsync(
                    db,
                    context.OperationId,
                    fingerprint,
                    cancellationToken
                );
                if (winner.Disposition is ManagementOperationDisposition.AlreadyCompleted)
                {
                    return ManagementOperationLog.Replay<MatchingMutationResponse>(winner.Record!);
                }
            }
        }

        var cap = Math.Min(
            request.MaxTotalOperations ?? options.Value.MaxTotalOperations,
            options.Value.MaxTotalOperations
        );

        var processed = await LoadProcessedCountAsync(db, context.OperationId, cancellationToken);
        var capped = false;
        ManagementCursor? cursor = null;

        while (true)
        {
            var budget = cap - processed;
            if (budget <= 0)
            {
                capped = true;
                break;
            }

            // Leave room for the bookkeeping commit and the reply: stopping one batch early beats
            // having the deadline fire between mutating rows and recording that we did.
            if (timeProvider.GetUtcNow() >= context.Deadline - BatchDeadlineMargin)
            {
                capped = true;
                break;
            }

            var batchSize = (int)Math.Min(ManagementPaging.MatchingBatchSize, budget);
            var outcome = await ApplyBatchAsync(
                db,
                request.Filter,
                cursor,
                batchSize,
                cancellationToken
            );

            if (outcome.Count == 0)
            {
                break;
            }

            var record = await TrackRecordAsync(db, context.OperationId, cancellationToken);
            if (record is null)
            {
                // The record was pruned or never written; without it the run is not attributable
                // and not resumable, so stop rather than mutate untracked.
                db.ChangeTracker.Clear();
                return ManagementResult.Conflict(
                    $"The operation record for '{context.OperationId}' is missing; the run was abandoned."
                );
            }

            record.ProcessedCount = processed + outcome.Count;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return ManagementResult.Conflict(
                    $"A row changed while this batch was being applied. {processed} rows were committed before it; "
                        + "retry with the same operation id to continue."
                );
            }

            processed = record.ProcessedCount;
            cursor = outcome.NextCursor;

            // Batches are committed, so the tracked graph has served its purpose. Holding ten
            // thousand entities to the end of the run would be the one thing that makes a bulk
            // operation fall over on memory.
            db.ChangeTracker.Clear();
        }

        var remaining = await CountAsync(db, request.Filter, cancellationToken);
        var response = new MatchingMutationResponse(processed, remaining, capped || remaining > 0);

        var final = await TrackRecordAsync(db, context.OperationId, cancellationToken);
        if (final is not null)
        {
            final.ProcessedCount = processed;
            operationLog.Complete(final, response);
            await db.SaveChangesAsync(cancellationToken);
        }

        return ManagementResult.Ok(response);
    }

    /// <summary>
    /// How close to the deadline the loop stops starting new batches.
    /// </summary>
    private static readonly TimeSpan BatchDeadlineMargin = TimeSpan.FromSeconds(2);

    private static async Task<long> LoadProcessedCountAsync(
        DbContext db,
        Guid operationId,
        CancellationToken cancellationToken
    ) =>
        await db.Set<ManagementOperationEntity>()
            .AsNoTracking()
            .Where(x => x.OperationId == operationId)
            .Select(x => x.ProcessedCount)
            .FirstOrDefaultAsync(cancellationToken);

    private static Task<ManagementOperationEntity?> TrackRecordAsync(
        DbContext db,
        Guid operationId,
        CancellationToken cancellationToken
    ) =>
        db.Set<ManagementOperationEntity>()
            .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
}

/// <summary>What one batch of a matching mutation did.</summary>
internal sealed record BatchOutcome(int Count, ManagementCursor? NextCursor);
