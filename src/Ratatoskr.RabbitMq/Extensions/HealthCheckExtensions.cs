using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ratatoskr.RabbitMq;

/// <summary>
/// Extension methods for configuring health checks in Ratatoskr.RabbitMq.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Registers the Ratatoskr RabbitMQ consumer health check.
    /// By default, a "ready" tag is applied.
    /// </summary>
    public static IHealthChecksBuilder AddRatatoskrRabbitMq(
        this IHealthChecksBuilder builder,
        string name = "ratatoskr-rabbitmq",
        HealthStatus? failureStatus = default,
        IEnumerable<string>? tags = default)
    {
        var tagList = tags?.ToList() ?? new List<string> { "ready" };
        if (!tagList.Contains("ready"))
        {
            tagList.Add("ready");
        }

        return builder.AddCheck<RabbitMqConsumerHealthCheck>(name, failureStatus, tagList);
    }
}
