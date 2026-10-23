using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Health check that reports unhealthy if the associated PollingBackgroundService has not
/// successfully processed within the specified threshold.
/// </summary>
internal sealed class ProcessorHealthCheck<TProcessor>(
    TProcessor processor,
    TimeProvider timeProvider,
    TimeSpan unhealthyThreshold
) : IHealthCheck
    where TProcessor : PollingBackgroundService
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var lastSuccess = processor.LastSuccessfulProcessingAt;
        var now = timeProvider.GetUtcNow();
        var timeSinceLastSuccess = now - lastSuccess;

        if (timeSinceLastSuccess > unhealthyThreshold)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{typeof(TProcessor).Name} has not processed successfully for {timeSinceLastSuccess.TotalSeconds:F1}s (threshold: {unhealthyThreshold.TotalSeconds}s)."
                    )
                )
            );
        }

        return Task.FromResult(
            HealthCheckResult.Healthy(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{typeof(TProcessor).Name} last processed successfully {timeSinceLastSuccess.TotalSeconds:F1}s ago."
                )
            )
        );
    }
}
