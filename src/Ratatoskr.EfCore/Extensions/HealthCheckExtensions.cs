using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods for configuring health checks in Ratatoskr.EfCore.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Registers the Ratatoskr Outbox processor health check.
    /// By default, a "ready" tag is applied.
    /// </summary>
    public static IHealthChecksBuilder AddRatatoskrOutbox<TDbContext>(
        this IHealthChecksBuilder builder,
        string? name = null,
        HealthStatus? failureStatus = default,
        IEnumerable<string>? tags = default,
        TimeSpan? unhealthyThreshold = null)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var checkName = name ?? $"ratatoskr-outbox-{typeof(TDbContext).Name}";
        var tagList = tags?.ToList() ?? new List<string> { "ready" };
        if (!tagList.Contains("ready"))
        {
            tagList.Add("ready");
        }

        return builder.Add(new HealthCheckRegistration(
            checkName,
            sp => new ProcessorHealthCheck<OutboxProcessor<TDbContext>>(
                sp.GetRequiredService<OutboxProcessor<TDbContext>>(),
                sp.GetRequiredService<TimeProvider>(),
                unhealthyThreshold ?? TimeSpan.FromMinutes(2)),
            failureStatus,
            tagList));
    }

    /// <summary>
    /// Registers the Ratatoskr Inbox processor health check.
    /// By default, a "ready" tag is applied.
    /// </summary>
    public static IHealthChecksBuilder AddRatatoskrInbox<TDbContext>(
        this IHealthChecksBuilder builder,
        string? name = null,
        HealthStatus? failureStatus = default,
        IEnumerable<string>? tags = default,
        TimeSpan? unhealthyThreshold = null)
        where TDbContext : DbContext, IInboxDbContext
    {
        var checkName = name ?? $"ratatoskr-inbox-{typeof(TDbContext).Name}";
        var tagList = tags?.ToList() ?? new List<string> { "ready" };
        if (!tagList.Contains("ready"))
        {
            tagList.Add("ready");
        }

        return builder.Add(new HealthCheckRegistration(
            checkName,
            sp => new ProcessorHealthCheck<InboxProcessor<TDbContext>>(
                sp.GetRequiredService<InboxProcessor<TDbContext>>(),
                sp.GetRequiredService<TimeProvider>(),
                unhealthyThreshold ?? TimeSpan.FromMinutes(2)),
            failureStatus,
            tagList));
    }
}
