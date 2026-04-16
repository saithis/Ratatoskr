namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Validates the shape of a bulk management request (either <c>Ids</c> or the
/// <c>All</c> flag, never both or neither, and never an oversized <c>Ids</c> list).
/// Centralised so the two Outbox/Inbox bulk endpoint pairs stay in lockstep.
/// </summary>
internal static class BulkRequestValidator
{
    /// <summary>
    /// Maximum number of ids a single bulk request may target. Chosen to fit
    /// comfortably inside typical SQL parameter limits (SQL Server: 2100,
    /// Postgres: unlimited but batching becomes slow past ~1000).
    /// </summary>
    internal const int MaxIds = 1000;

    internal static bool TryValidate(IReadOnlyList<Guid>? ids, bool? all, out string? error)
    {
        // Ambiguous: caller mixed the two exclusive modes.
        if (all is true && ids is { Count: > 0 })
        {
            error = $"'{nameof(all)}' and '{nameof(ids)}' are mutually exclusive — send exactly one.";
            return false;
        }

        if (all is true)
        {
            error = null;
            return true;
        }

        if (ids is null || ids.Count == 0)
        {
            error = $"Provide a non-empty '{nameof(ids)}' list or set '{nameof(all)}' to true.";
            return false;
        }

        if (ids.Count > MaxIds)
        {
            error = $"'{nameof(ids)}' has {ids.Count} entries which exceeds the limit of {MaxIds}.";
            return false;
        }

        if (ids.Any(id => id == Guid.Empty))
        {
            error = $"'{nameof(ids)}' contains an empty Guid.";
            return false;
        }

        error = null;
        return true;
    }
}
