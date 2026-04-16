namespace Ratatoskr.EfCore.Management;

internal static class PaginationOptions
{
    internal const int DefaultPageSize = 50;
    internal const int MinPageSize = 1;
    internal const int MaxPageSize = 200;

    /// <summary>
    /// Returns a bounded page size. Values outside the allowed range are clamped
    /// rather than rejected so that callers with a default-configured client (for
    /// example a backend proxy) never break on a policy change.
    /// </summary>
    internal static int ClampPageSize(int pageSize) =>
        Math.Clamp(pageSize, MinPageSize, MaxPageSize);
}
