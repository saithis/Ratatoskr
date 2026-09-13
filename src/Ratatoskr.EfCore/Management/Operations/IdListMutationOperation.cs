using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Idempotency;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>
/// The shared shape of "mutate exactly these ids": validate the list, check the operation log,
/// apply, and record the outcome in the same transaction as the change.
/// </summary>
/// <remarks>
/// Subclasses supply only the mutation itself. Everything that makes the mutation safe to retry —
/// the idempotency record, the replay, the mismatch refusal, the concurrency handling — lives here
/// once, because a mutation that gets those wrong is a mutation that deletes twice.
/// </remarks>
internal abstract class IdListMutationOperation(
    ManagementResourceResolver resolver,
    ManagementOperationLog operationLog
) : IManagementOperation
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public Type RequestType => typeof(MutateByIdsRequest);

    /// <summary>Which half of the durability model this mutation needs.</summary>
    protected abstract DurabilityFeature Feature { get; }

    /// <summary>
    /// Applies the mutation to the tracked entities, without saving. The base class owns
    /// <c>SaveChangesAsync</c> so the idempotency record commits atomically with the change.
    /// </summary>
    protected abstract Task<IdListMutationPlan> PlanAsync(
        DbContext db,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken
    );

    /// <inheritdoc />
    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<MutateByIdsRequest>();

        if (resolver.Resolve(context, Feature, out var db) is { } failure)
        {
            return failure;
        }

        if (ValidateIds(request.Ids) is { } invalid)
        {
            return invalid;
        }

        var ids = request.Ids.Distinct().Order().ToArray();
        var fingerprint = ManagementOperationLog.Serialize(ids);

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

        var plan = await PlanAsync(db, ids, cancellationToken);
        var response = new MutationResponse(plan.Succeeded, plan.Failed);

        if (lookup.Record is { } resumed)
        {
            // Only reachable if a previous attempt crashed between staging and commit, which a
            // single SaveChanges makes vanishingly unlikely — but reusing the row keeps the
            // primary key honest instead of failing the retry on a duplicate insert.
            resumed.ProcessedCount = plan.Succeeded.Count;
            operationLog.Complete(resumed, response);
        }
        else
        {
            operationLog.StageCompleted(
                db,
                context,
                response,
                plan.Succeeded.Count,
                fingerprint
            );
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // SaveChanges wraps the batch in one transaction, so a conflict rolled back every row
            // including the idempotency record. Reporting partial success here would be a lie.
            db.ChangeTracker.Clear();
            return ManagementResult.Conflict(
                "One or more rows were modified concurrently, so nothing in this batch was applied. Retry with the same operation id."
            );
        }
        catch (DbUpdateException)
        {
            // Two deliveries of the same command can race; the loser fails on the operation-id
            // primary key. That is the mechanism working, not an error, so read back what the
            // winner recorded and answer with it.
            db.ChangeTracker.Clear();
            var winner = await operationLog.LookupAsync(
                db,
                context.OperationId,
                fingerprint,
                cancellationToken
            );

            if (winner.Disposition is ManagementOperationDisposition.AlreadyCompleted)
            {
                return ManagementOperationLog.Replay<MutationResponse>(winner.Record!);
            }

            throw;
        }

        return ManagementResult.Ok(response);
    }

    private static ManagementResult? ValidateIds(IReadOnlyList<Guid> ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return ManagementResult.Invalid(
                "Provide a non-empty 'ids' list, or use the matching variant of this operation with a filter.",
                ManagementErrorCodes.FilterRequired
            );
        }

        if (ids.Count > ManagementPaging.MaxMutationIds)
        {
            return ManagementResult.Invalid(
                $"'ids' has {ids.Count} entries, which exceeds the limit of {ManagementPaging.MaxMutationIds}."
            );
        }

        return ids.Any(id => id == Guid.Empty)
            ? ManagementResult.Invalid("'ids' contains an empty Guid.")
            : null;
    }
}

/// <summary>The outcome of applying an explicit-id mutation, before it is committed.</summary>
internal sealed record IdListMutationPlan(
    IReadOnlyList<Guid> Succeeded,
    IReadOnlyList<MutationFailure> Failed
);
