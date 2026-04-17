namespace Ratatoskr.Management;

internal static class PaginationOptions
{
    internal const int DefaultPageSize = 50;
    internal const int MinPageSize = 1;
    internal const int MaxPageSize = 200;

    internal static int ClampPageSize(int pageSize) =>
        Math.Clamp(pageSize, MinPageSize, MaxPageSize);
}
