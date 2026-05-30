namespace Ratatoskr.Management;

#pragma warning disable MA0182 // used by Ratatoskr.EfCore via InternalsVisibleTo
internal static class PaginationOptions
#pragma warning restore MA0182
{
    internal const int DefaultPageSize = 50;
    internal const int MinPageSize = 1;
    internal const int MaxPageSize = 200;

    internal static int ClampPageSize(int pageSize) =>
        Math.Clamp(pageSize, MinPageSize, MaxPageSize);
}
