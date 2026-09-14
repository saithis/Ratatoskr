using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Http;

namespace Ratatoskr.UI.Store;

/// <summary>
/// Wraps the dashboard's dispatch so that every mutation leaves a record of who did what, to
/// which service, with which filter, and what came back.
/// </summary>
/// <remarks>
/// Auditing sits here rather than inside each operation because this is the layer that knows the
/// answer to "who": the actor comes from the HTTP principal, and the record has to be written
/// even when the target service never replies.
/// </remarks>
internal sealed partial class AuditingManagementDispatchStrategy(
    TransportManagementDispatchStrategy inner,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AuditingManagementDispatchStrategy> logger
) : IManagementDispatchStrategy
{
    /// <summary>
    /// Reading is not audited: a dashboard that wrote a row for every list refresh would bury the
    /// mutations nobody could then find.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyOperations = new(StringComparer.Ordinal)
    {
        ManagementOperationNames.ServiceDescribe,
        ManagementOperationNames.ContextsList,
        ManagementOperationNames.ContextHealth,
        ManagementOperationNames.OutboxList,
        ManagementOperationNames.OutboxGet,
        ManagementOperationNames.OutboxCount,
        ManagementOperationNames.InboxList,
        ManagementOperationNames.InboxGet,
        ManagementOperationNames.InboxCount,
        ManagementOperationNames.QueueStats,
    };

    public async Task<ManagementResponseEnvelope> ExecuteAsync(
        HttpContext http,
        string operation,
        object request,
        CancellationToken cancellationToken
    )
    {
        if (ReadOnlyOperations.Contains(operation))
        {
            return await inner.ExecuteAsync(http, operation, request, cancellationToken);
        }

        var startedAt = timeProvider.GetUtcNow();
        var response = await inner.ExecuteAsync(http, operation, request, cancellationToken);

        await WriteAsync(http, operation, request, startedAt, response);
        return response;
    }

    private async Task WriteAsync(
        HttpContext http,
        string operation,
        object request,
        DateTimeOffset startedAt,
        ManagementResponseEnvelope response
    )
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RatatoskrDashboardDbContext>();
            var actor = ManagementHttp.ActorFrom(http);

            db.AuditEntries.Add(
                new DashboardAuditEntry
                {
                    Id = Guid.NewGuid(),
                    OperationId = response.OperationId,
                    TransportName = ManagementHttp.RouteValue(http, "transport") ?? string.Empty,
                    ServiceName = ManagementHttp.RouteValue(http, "serviceName") ?? string.Empty,
                    InstanceId = http.Request.Query["instanceId"].FirstOrDefault(),
                    Resource = ManagementHttp.RouteValue(http, "contextName"),
                    Operation = operation,
                    Actor = actor?.Subject,
                    ActorDisplayName = actor?.DisplayName,
                    RequestJson = JsonSerializer.Serialize(
                        request,
                        request.GetType(),
                        ManagementJson.Options
                    ),
                    StartedAt = startedAt,
                    CompletedAt = timeProvider.GetUtcNow(),
                    Outcome = response.Status.ToString(),
                    ErrorCode = response.Error?.Code,
                    ResultJson = response.Payload?.GetRawText(),
                }
            );

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The mutation has already happened; failing the caller's request now would be worse
            // than a gap in the audit trail, which is at least visible in the log.
            LogAuditFailed(logger, operation, ex);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "The audit record for management operation {Operation} could not be written."
    )]
    private static partial void LogAuditFailed(
        ILogger logger,
        string operation,
        Exception exception
    );
}

/// <summary>
/// Prunes audit entries past their retention, in bounded batches.
/// </summary>
/// <remarks>
/// The audit table grows without bound by construction — one row per mutation, forever — so
/// retention is part of the decision to require a dashboard database, not an afterthought.
/// </remarks>
internal sealed partial class DashboardAuditCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<ManagementDashboardOptions> options,
    TimeProvider timeProvider,
    ILogger<DashboardAuditCleanupService> logger
) : BackgroundService
{
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.Value.AuditCleanupInterval, timeProvider, stoppingToken);

            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogCleanupFailed(logger, ex);
            }
        }
    }

    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RatatoskrDashboardDbContext>();

        var cutoff = timeProvider.GetUtcNow() - options.Value.AuditRetention;
        var removed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var deleted = await db
                .AuditEntries.Where(entry => entry.StartedAt < cutoff)
                .OrderBy(entry => entry.StartedAt)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            removed += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        if (removed > 0)
        {
            LogPruned(logger, removed);
        }

        var snapshotCutoff = timeProvider.GetUtcNow() - options.Value.SnapshotRetention;
        var prunedSnapshots = await db
            .ServiceSnapshots.Where(entry => entry.LastSeenAt < snapshotCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        if (prunedSnapshots > 0)
        {
            LogSnapshotsPruned(logger, prunedSnapshots);
        }

        return removed;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Pruned {Count} audit entries.")]
    private static partial void LogPruned(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Audit retention failed.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Pruned {Count} stale service snapshots.")]
    private static partial void LogSnapshotsPruned(ILogger logger, int count);
}
