namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Calculates exponential backoff with equal jitter for retry delays.
/// Shared by both inbox and outbox entity retry logic.
/// </summary>
internal static class BackoffCalculator
{
    /// <summary>
    /// Returns the next retry delay using exponential backoff with equal jitter:
    /// base/2 + random(0, base/2). Prevents thundering herd while maintaining
    /// a predictable minimum delay.
    /// </summary>
    /// <param name="errorCount">Number of errors so far (determines backoff exponent).</param>
    /// <param name="maxRetryDelay">Maximum delay cap.</param>
    /// <param name="random">Optional Random instance for deterministic testing. Do not share across threads.</param>
    public static TimeSpan CalculateDelay(int errorCount, TimeSpan maxRetryDelay, Random? random = null)
    {
        var baseDelay = Math.Max(0, Math.Min(Math.Pow(2, errorCount), maxRetryDelay.TotalSeconds));
        var delaySeconds = baseDelay * 0.5 + baseDelay * 0.5 * (random ?? Random.Shared).NextDouble();
        return TimeSpan.FromSeconds(delaySeconds);
    }
}
