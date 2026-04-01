using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ratatoskr.RabbitMq;

internal sealed class RabbitMqConsumerHealthCheck(RabbitMqConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, 
        CancellationToken cancellationToken = default)
    {
        // Check if consumer is running and channels are healthy
        if (consumer.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy("RabbitMQ consumer is running"));
        }
        
        return Task.FromResult(HealthCheckResult.Unhealthy("RabbitMQ consumer is not healthy"));
    }
}
