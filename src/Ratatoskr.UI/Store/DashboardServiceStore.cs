using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;

namespace Ratatoskr.UI.Store;

/// <summary>
/// Keeps the persistent snapshot table in step with the in-memory registry, in both directions.
/// </summary>
/// <remarks>
/// On start it loads what was last seen into the registry, so a restarted dashboard renders the
/// fleet straight away with entries marked stale by age. From then on it writes through: every
/// announcement the registry accepts is persisted, so the next restart is just as quick.
/// <para>
/// This is also what removes a whole class of bug rather than one instance of it — the in-memory
/// view can no longer be the only copy, so "the dashboard went blank" stops being reachable by
/// any pruning or expiry policy.
/// </para>
/// </remarks>
internal sealed partial class DashboardServiceStore(
    IServiceScopeFactory scopeFactory,
    ServiceRegistry registry,
    TimeProvider timeProvider,
    ILogger<DashboardServiceStore> logger
) : IHostedService, IDisposable
{
    private bool _subscribed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RatatoskrDashboardDbContext>();

        var stored = await db.ServiceSnapshots.AsNoTracking().ToListAsync(cancellationToken);
        foreach (var snapshot in stored)
        {
            var announcement = Deserialize(snapshot);
            if (announcement is not null)
            {
                registry.Publish(snapshot.TransportName, announcement);
            }
        }

        LogRestored(logger, stored.Count);

        registry.ServiceUpdated += OnServiceUpdated;
        _subscribed = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose() => Unsubscribe();

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            registry.ServiceUpdated -= OnServiceUpdated;
            _subscribed = false;
        }
    }

    private void OnServiceUpdated(object? sender, ServiceAnnouncementEventArgs args) =>
        _ = PersistAsync(args.TransportName, args.Announcement);

    private async Task PersistAsync(string transportName, ServiceAnnouncement announcement)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RatatoskrDashboardDbContext>();

            var existing = await db.ServiceSnapshots.FindAsync(
                [transportName, announcement.ServiceName, announcement.InstanceId],
                CancellationToken.None
            );

            var now = timeProvider.GetUtcNow();
            var json = JsonSerializer.Serialize(announcement, ManagementJson.Options);

            if (existing is null)
            {
                db.ServiceSnapshots.Add(
                    new DashboardServiceSnapshot
                    {
                        TransportName = transportName,
                        ServiceName = announcement.ServiceName,
                        InstanceId = announcement.InstanceId,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        AnnouncedAt = announcement.AnnouncedAt,
                        AnnouncementJson = json,
                    }
                );
            }
            else
            {
                existing.LastSeenAt = now;
                existing.AnnouncedAt = announcement.AnnouncedAt;
                existing.AnnouncementJson = json;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A store that cannot keep up must never stop the dashboard from showing live data.
            // The in-memory registry already has the announcement; this only costs a faster
            // restart next time.
            LogPersistFailed(logger, transportName, announcement.ServiceName, ex);
        }
    }

    private ServiceAnnouncement? Deserialize(DashboardServiceSnapshot snapshot)
    {
        try
        {
            return JsonSerializer.Deserialize<ServiceAnnouncement>(
                snapshot.AnnouncementJson,
                ManagementJson.Options
            );
        }
        catch (JsonException ex)
        {
            // Written by an older protocol version, most likely. One unreadable row must not stop
            // the rest of the fleet from being restored.
            LogUnreadableSnapshot(logger, snapshot.TransportName, snapshot.ServiceName, ex);
            return null;
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Restored {Count} service snapshots from the dashboard store."
    )]
    private static partial void LogRestored(ILogger logger, int count);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Could not persist the announcement for {ServiceName} on transport {TransportName}."
    )]
    private static partial void LogPersistFailed(
        ILogger logger,
        string transportName,
        string serviceName,
        Exception exception
    );

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "A stored snapshot for {ServiceName} on transport {TransportName} could not be read and was skipped."
    )]
    private static partial void LogUnreadableSnapshot(
        ILogger logger,
        string transportName,
        string serviceName,
        Exception exception
    );
}
