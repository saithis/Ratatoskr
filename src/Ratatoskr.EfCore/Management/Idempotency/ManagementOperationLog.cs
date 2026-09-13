using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.EfCore.Idempotency;

/// <summary>What the operation log knows about an operation id that has been seen before.</summary>
internal enum ManagementOperationDisposition
{
    /// <summary>Never seen. Run it.</summary>
    New,

    /// <summary>Seen and finished. Replay the recorded result rather than mutating again.</summary>
    AlreadyCompleted,

    /// <summary>Seen, started, not finished. Continue accumulating into the same record.</summary>
    Resumable,

    /// <summary>Seen with a different filter. Refuse.</summary>
    FilterMismatch,
}

/// <summary>The log's answer about one operation id.</summary>
internal sealed record ManagementOperationLookup(
    ManagementOperationDisposition Disposition,
    ManagementOperationEntity? Record
);

/// <summary>
/// Reads and writes the per-service record of mutations, which is what makes an at-least-once
/// control plane safe to retry.
/// </summary>
internal sealed class ManagementOperationLog(TimeProvider timeProvider)
{
    /// <summary>Looks up what is already known about <paramref name="operationId"/>.</summary>
    public async Task<ManagementOperationLookup> LookupAsync(
        DbContext db,
        Guid operationId,
        string? filterJson,
        CancellationToken cancellationToken
    )
    {
        var existing = await db.Set<ManagementOperationEntity>()
            .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);

        if (existing is null)
        {
            return new ManagementOperationLookup(ManagementOperationDisposition.New, Record: null);
        }

        // An operation id is the caller's promise that "this is the same request". Honouring that
        // promise for a request whose filter changed would apply a mutation the caller never
        // previewed, so a mismatch is refused rather than reconciled.
        if (!string.Equals(existing.FilterJson, filterJson, StringComparison.Ordinal))
        {
            return new ManagementOperationLookup(
                ManagementOperationDisposition.FilterMismatch,
                existing
            );
        }

        return new ManagementOperationLookup(
            existing.State is ManagementOperationState.Completed
                ? ManagementOperationDisposition.AlreadyCompleted
                : ManagementOperationDisposition.Resumable,
            existing
        );
    }

    /// <summary>
    /// Stages a completed record for a single-transaction mutation. The caller saves it together
    /// with its entity changes, so the record and the mutation land or roll back as one.
    /// </summary>
    public ManagementOperationEntity StageCompleted<TResult>(
        DbContext db,
        ManagementOperationContext context,
        TResult result,
        long processedCount,
        string? filterJson = null
    )
    {
        var now = timeProvider.GetUtcNow();
        var record = new ManagementOperationEntity
        {
            OperationId = context.OperationId,
            CreatedAt = now,
            CompletedAt = now,
            Operation = context.Operation,
            Actor = context.Actor?.Subject ?? context.Actor?.DisplayName,
            State = ManagementOperationState.Completed,
            FilterJson = filterJson,
            ProcessedCount = processedCount,
            ResultJson = JsonSerializer.Serialize(result, ManagementJson.Options),
        };

        db.Set<ManagementOperationEntity>().Add(record);
        return record;
    }

    /// <summary>
    /// Stages an in-progress record for a mutation that commits in several batches. Writing it
    /// completed up front would mark a partially applied operation as done; writing it only at the
    /// end would leave a crash-interrupted run with no record at all.
    /// </summary>
    public ManagementOperationEntity StageInProgress(
        DbContext db,
        ManagementOperationContext context,
        string filterJson
    )
    {
        var record = new ManagementOperationEntity
        {
            OperationId = context.OperationId,
            CreatedAt = timeProvider.GetUtcNow(),
            Operation = context.Operation,
            Actor = context.Actor?.Subject ?? context.Actor?.DisplayName,
            State = ManagementOperationState.InProgress,
            FilterJson = filterJson,
            ProcessedCount = 0,
        };

        db.Set<ManagementOperationEntity>().Add(record);
        return record;
    }

    /// <summary>Marks a multi-batch record finished and records the result to replay.</summary>
    public void Complete<TResult>(ManagementOperationEntity record, TResult result)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.State = ManagementOperationState.Completed;
        record.CompletedAt = timeProvider.GetUtcNow();
        record.ResultJson = JsonSerializer.Serialize(result, ManagementJson.Options);
    }

    /// <summary>Replays a recorded result, or reports that it cannot be read back.</summary>
    public static ManagementResult Replay<TResult>(ManagementOperationEntity record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var replayed = record.ResultJson is null
            ? default
            : JsonSerializer.Deserialize<TResult>(record.ResultJson, ManagementJson.Options);

        return replayed is null
            ? ManagementResult.Conflict(
                $"Operation '{record.OperationId}' already ran but its result could not be replayed."
            )
            : ManagementResult.Ok(replayed);
    }

    /// <summary>Serializes a filter for the mismatch check. Null for operations without one.</summary>
    public static string Serialize(MessageFilter filter) =>
        JsonSerializer.Serialize(filter, ManagementJson.Options);

    /// <summary>Serializes an explicit id list for the mismatch check.</summary>
    public static string Serialize(IReadOnlyList<Guid> ids) =>
        JsonSerializer.Serialize(ids.Order().ToArray(), ManagementJson.Options);

    /// <summary>The failure returned when a known operation id arrives with a different request.</summary>
    public static ManagementResult FilterMismatch(Guid operationId) =>
        ManagementResult.Conflict(
            $"Operation id '{operationId}' was already used for a different request. "
                + "Reuse an operation id only to retry the identical request.",
            ManagementErrorCodes.Conflict
        );
}
