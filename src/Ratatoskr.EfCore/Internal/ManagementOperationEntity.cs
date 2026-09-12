namespace Ratatoskr.EfCore.Internal;

/// <summary>How far a recorded management operation got.</summary>
internal enum ManagementOperationState
{
    /// <summary>Started, and at least one more batch is expected.</summary>
    InProgress = 0,

    /// <summary>Finished. <see cref="ManagementOperationEntity.ResultJson"/> is the answer to replay.</summary>
    Completed = 1,
}

/// <summary>
/// One record per management mutation, written in the same transaction as the mutation itself.
/// </summary>
/// <remarks>
/// Control-plane delivery is at least once, so the same command arrives twice whenever a response
/// is lost or an agent restarts mid-flight. This row is what makes the second arrival a replay
/// instead of a second deletion: the primary key is the caller's operation id, so a duplicate
/// either loses the insert race or finds the completed row and returns the recorded result.
/// <para>
/// It doubles as the attribution record — who ran which mutation, with which filter, and when —
/// which is what a post-incident review actually needs from the service side.
/// </para>
/// </remarks>
internal sealed class ManagementOperationEntity
{
    /// <summary>The caller's operation id. Primary key; that is the whole mechanism.</summary>
    public Guid OperationId { get; set; }

    /// <summary>When the agent first saw this operation.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it finished, or null while it is still in progress.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The operation name, for attribution and for cleanup diagnostics.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Who asked, as carried on the request envelope.</summary>
    public string? Actor { get; set; }

    /// <summary>Whether the operation finished.</summary>
    public ManagementOperationState State { get; set; }

    /// <summary>
    /// The serialized filter, for multi-batch operations. A replay of the same operation id with a
    /// different filter is a caller bug or an id collision, and is refused rather than silently
    /// applying a different mutation to whatever the second filter happens to match.
    /// </summary>
    public string? FilterJson { get; set; }

    /// <summary>
    /// Rows mutated so far, accumulated across batches. Exists so the reported total stays
    /// accurate when a run is interrupted and resumed, not to prevent double mutation — the
    /// mutations narrow their own predicate, so re-running a filter picks up only what is left.
    /// </summary>
    public long ProcessedCount { get; set; }

    /// <summary>The recorded result to replay, set when <see cref="State"/> becomes Completed.</summary>
    public string? ResultJson { get; set; }
}
