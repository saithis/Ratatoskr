using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.RabbitMq.WolverineMigration;

/// <summary>
/// Health check that verifies both Wolverine and Ratatoskr topologies are healthy during migration.
/// </summary>
internal class WolverineMigrationHealthCheck(
    WolverineMigrationConsumer consumer,
    RabbitMqConnectionManager connectionManager,
    WolverineMigrationOptions migrationOptions,
    ILogger<WolverineMigrationHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Check if migration consumer is running and channels are healthy
            if (!consumer.IsHealthy)
            {
                return HealthCheckResult.Unhealthy("Migration consumer is not healthy");
            }

            // 2. Check RabbitMQ connection
            var connectionHealthy = await CheckConnectionHealthAsync(cancellationToken);
            if (!connectionHealthy)
            {
                return HealthCheckResult.Unhealthy("RabbitMQ connection is not healthy");
            }

            // 3. Verify both old and new topologies exist (if validation is enabled)
            if (migrationOptions.ValidateWolverineTopologyExists)
            {
                var topologyHealthy = await ValidateTopologiesExistAsync(cancellationToken);
                if (topologyHealthy.Status != HealthStatus.Healthy)
                {
                    return topologyHealthy;
                }
            }

            var data = new Dictionary<string, object>
            {
                { "migration_enabled", migrationOptions.EnableMigration },
                { "queue_suffix", migrationOptions.QueueSuffix },
                { "queue_mappings", migrationOptions.QueueMappings.Count },
                { "consumer_healthy", consumer.IsHealthy }
            };

            return HealthCheckResult.Healthy(
                "Wolverine migration is healthy. Both old and new topologies are operational.",
                data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check failed");
            return HealthCheckResult.Unhealthy(
                "Wolverine migration health check failed",
                ex);
        }
    }

    private async Task<bool> CheckConnectionHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await connectionManager.CreateChannelAsync(true, cancellationToken);
            return channel.IsOpen;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RabbitMQ connection health check failed");
            return false;
        }
    }

    private async Task<HealthCheckResult> ValidateTopologiesExistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await connectionManager.CreateChannelAsync(true, cancellationToken);

            var missingOldQueues = new List<string>();
            var missingNewQueues = new List<string>();

            foreach (var mapping in migrationOptions.QueueMappings)
            {
                var oldQueueName = mapping.Key;
                var newQueueBaseName = mapping.Value;
                var newQueueName = $"{newQueueBaseName}{migrationOptions.QueueSuffix}";

                // Check old Wolverine queue exists
                try
                {
                    await channel.QueueDeclarePassiveAsync(oldQueueName, cancellationToken);
                }
                catch
                {
                    missingOldQueues.Add(oldQueueName);
                    logger.LogWarning("Old Wolverine queue '{Queue}' does not exist", oldQueueName);
                }

                // Check new Ratatoskr queue exists
                try
                {
                    await channel.QueueDeclarePassiveAsync(newQueueName, cancellationToken);
                }
                catch
                {
                    missingNewQueues.Add(newQueueName);
                    logger.LogWarning("New Ratatoskr queue '{Queue}' does not exist", newQueueName);
                }
            }

            if (missingOldQueues.Count > 0 || missingNewQueues.Count > 0)
            {
                var data = new Dictionary<string, object>();
                
                if (missingOldQueues.Count > 0)
                {
                    data["missing_old_queues"] = missingOldQueues;
                }
                
                if (missingNewQueues.Count > 0)
                {
                    data["missing_new_queues"] = missingNewQueues;
                }

                return HealthCheckResult.Degraded(
                    "Some queues are missing during migration",
                    data: data);
            }

            return HealthCheckResult.Healthy("All migration queues exist");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Topology validation failed");
            return HealthCheckResult.Unhealthy(
                "Failed to validate topology existence",
                ex);
        }
    }
}
