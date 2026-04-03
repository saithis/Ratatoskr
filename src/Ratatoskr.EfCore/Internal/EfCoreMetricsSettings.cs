namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Per-<typeparamref name="TDbContext"/> polling and query timeout for EF Core backlog gauges.
/// </summary>
internal sealed class EfCoreMetricsSettings<TDbContext>
{
    public static EfCoreMetricsSettings<TDbContext> Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5));

    public TimeSpan PollingInterval { get; }
    public TimeSpan QueryTimeout { get; }

    public EfCoreMetricsSettings(TimeSpan pollingInterval, TimeSpan queryTimeout)
    {
        PollingInterval = pollingInterval;
        QueryTimeout = queryTimeout;
    }
}
