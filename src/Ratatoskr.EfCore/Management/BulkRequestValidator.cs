namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Validates the shape of a bulk management request. The "all" and "by ids" cases live on
/// separate routes, so this validator only has to sanity-check the id list — centralised
/// so every bulk endpoint applies the same bounds.
/// </summary>
internal static class BulkRequestValidator
{
    /// <summary>
    /// Maximum number of ids a single bulk request may target. Chosen to fit
    /// comfortably inside typical SQL parameter limits (SQL Server: 2100,
    /// Postgres: unlimited but batching becomes slow past ~1000).
    /// </summary>
    internal const int MaxIds = 1000;

    internal static bool TryValidateIds(IReadOnlyList<Guid>? ids, out string? error)
    {
        if (ids is null || ids.Count == 0)
        {
            error = $"Provide a non-empty '{nameof(ids)}' list, or use the '/all' variant of this endpoint.";
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
