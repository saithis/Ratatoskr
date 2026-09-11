using System.Collections.Concurrent;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Runtime;

/// <summary>
/// Maintains an immutable, time-bounded view of service instance announcements.
/// Metrics are treated as instance-local and are summed for a logical service.
/// </summary>
public sealed class ServiceRegistry(TimeProvider timeProvider, TimeSpan offlineThreshold)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ServiceHeartbeat>> _services = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<ServiceHeartbeatEventArgs>? ServiceUpdated;

    /// <summary>Adds an announcement unless it is older than the instance snapshot already held.</summary>
    public bool Publish(ServiceHeartbeat announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var snapshot = Snapshot(announcement);
        var instances = _services.GetOrAdd(snapshot.ServiceName, static _ => new(StringComparer.Ordinal));

        while (true)
        {
            if (instances.TryGetValue(snapshot.InstanceId, out var existing) && existing.Timestamp >= snapshot.Timestamp)
            {
                return false;
            }

            if (existing is null
                ? instances.TryAdd(snapshot.InstanceId, snapshot)
                : instances.TryUpdate(snapshot.InstanceId, snapshot, existing))
            {
                PruneExpired();
                ServiceUpdated?.Invoke(this, new ServiceHeartbeatEventArgs(snapshot));
                return true;
            }
        }
    }

    public IReadOnlyList<ServiceCardDto> GetAllServices()
    {
        PruneExpired();
        return _services
            .Select(pair => ToCard(pair.Key, pair.Value.Values, timeProvider.GetUtcNow(), offlineThreshold))
            .OrderBy(card => card.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ServiceDetailDto? GetService(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        PruneExpired();
        return _services.TryGetValue(serviceName, out var instances)
            ? ToDetail(serviceName, instances.Values, timeProvider.GetUtcNow(), offlineThreshold)
            : null;
    }

    private void PruneExpired()
    {
        var cutoff = timeProvider.GetUtcNow() - offlineThreshold;
        foreach (var (serviceName, instances) in _services)
        {
            foreach (var (instanceId, heartbeat) in instances)
            {
                if (heartbeat.Timestamp < cutoff)
                {
                    instances.TryRemove(instanceId, out _);
                }
            }

            if (instances.IsEmpty)
            {
                _services.TryRemove(serviceName, out _);
            }
        }
    }

    private static ServiceCardDto ToCard(string serviceName, IEnumerable<ServiceHeartbeat> values, DateTimeOffset now, TimeSpan threshold)
    {
        var instances = values.ToArray();
        var active = instances.Where(x => x.Timestamp >= now - threshold).ToArray();
        var metricSource = active.Length > 0 ? active : instances;
        return new ServiceCardDto(serviceName, active.Length > 0 ? "online" : "stale", active.Length,
            metricSource.Sum(x => x.DbContexts.Sum(d => d.PendingOutboxCount)),
            metricSource.Sum(x => x.DbContexts.Sum(d => d.PoisonedOutboxCount)),
            metricSource.Sum(x => x.DbContexts.Sum(d => d.PendingInboxCount)),
            metricSource.Sum(x => x.DbContexts.Sum(d => d.PoisonedInboxCount)),
            instances.Max(x => x.Timestamp),
            metricSource.SelectMany(x => x.DbContexts).Select(x => x.DbContextName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static ServiceDetailDto ToDetail(string serviceName, IEnumerable<ServiceHeartbeat> values, DateTimeOffset now, TimeSpan threshold)
    {
        var instances = values.OrderBy(x => x.InstanceId, StringComparer.Ordinal).ToArray();
        var latest = instances.OrderByDescending(x => x.Timestamp).First();
        return new ServiceDetailDto(serviceName,
            instances.Any(x => x.Timestamp >= now - threshold) ? "online" : "stale",
            instances.Select(x => new ServiceInstanceRecordDto(x.InstanceId, x.MachineName, x.Environment, x.StartedAt, x.Timestamp, x.Timestamp >= now - threshold)).ToArray(),
            Snapshot(latest).DbContexts,
            Snapshot(latest).Channels);
    }

    private static ServiceHeartbeat Snapshot(ServiceHeartbeat source) => source with
    {
        Capabilities = [.. source.Capabilities],
        DbContexts = [.. source.DbContexts.Select(x => x with { })],
        Channels = [.. source.Channels.Select(x => x with
        {
            MessageTypes = [.. x.MessageTypes],
            TransportBindings = [.. x.TransportBindings.Select(binding => binding with
            {
                Properties = new Dictionary<string, string>(binding.Properties, StringComparer.Ordinal)
            })]
        })]
    };
}
