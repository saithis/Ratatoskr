using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.UI.Client;

public sealed record ServiceCardDto(
    string ServiceName,
    string Status,
    int InstanceCount,
    long TotalPendingOutbox,
    long TotalPoisonedOutbox,
    long TotalPendingInbox,
    long TotalPoisonedInbox,
    DateTimeOffset LastHeartbeat,
    IReadOnlyList<string> DbContextNames
);

public sealed record ServiceInstanceRecordDto(
    string InstanceId,
    string MachineName,
    string? Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeat,
    bool IsActive
);

public sealed record ServiceDetailDto(
    string ServiceName,
    string Status,
    IReadOnlyList<ServiceInstanceRecordDto> Instances,
    IReadOnlyList<DbContextSummaryDto> DbContexts,
    IReadOnlyList<ChannelSummaryDto> Channels
);

/// <summary>
/// Maintains active registered services, their replica instances, and latest known metrics.
/// </summary>
public sealed class ActiveServiceRegistry(IOptions<RatatoskrUiOptions> options)
{
    private readonly ConcurrentDictionary<string, ServiceState> _services = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ServiceHeartbeat>? OnServiceUpdated;

    public void RegisterHeartbeat(ServiceHeartbeat heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        var state = _services.GetOrAdd(heartbeat.ServiceName, name => new ServiceState(name));
        state.Update(heartbeat);
        OnServiceUpdated?.Invoke(heartbeat);
    }

    public IReadOnlyList<ServiceCardDto> GetAllServices()
    {
        var threshold = options.Value.ServiceOfflineThreshold;
        var now = DateTimeOffset.UtcNow;

        return _services.Values.Select(s => s.ToCardDto(threshold, now)).ToList();
    }

    public ServiceDetailDto? GetService(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!_services.TryGetValue(serviceName, out var state))
        {
            return null;
        }

        return state.ToDetailDto(options.Value.ServiceOfflineThreshold, DateTimeOffset.UtcNow);
    }

    private sealed class ServiceState(string serviceName)
    {
        public string ServiceName { get; } = serviceName;
        private readonly ConcurrentDictionary<string, InstanceState> _instances = new(StringComparer.OrdinalIgnoreCase);
        private List<DbContextSummaryDto> _dbContexts = [];
        private List<ChannelSummaryDto> _channels = [];
        private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;

        public void Update(ServiceHeartbeat heartbeat)
        {
            _lastHeartbeat = heartbeat.Timestamp;
            _dbContexts = heartbeat.DbContexts;
            _channels = heartbeat.Channels;

            var inst = _instances.GetOrAdd(heartbeat.InstanceId, id => new InstanceState(id));
            inst.Update(heartbeat);
        }

        public ServiceCardDto ToCardDto(TimeSpan threshold, DateTimeOffset now)
        {
            var activeInstances = _instances.Values.Count(i => now - i.LastHeartbeat <= threshold);
            var isOnline = activeInstances > 0;
            var isStale = !isOnline && now - _lastHeartbeat <= threshold * 2;
            var status = isOnline ? "online" : (isStale ? "stale" : "offline");

            var totalPendingOutbox = _dbContexts.Sum(d => d.PendingOutboxCount);
            var totalPoisonedOutbox = _dbContexts.Sum(d => d.PoisonedOutboxCount);
            var totalPendingInbox = _dbContexts.Sum(d => d.PendingInboxCount);
            var totalPoisonedInbox = _dbContexts.Sum(d => d.PoisonedInboxCount);

            return new ServiceCardDto(
                ServiceName,
                status,
                activeInstances > 0 ? activeInstances : _instances.Count,
                totalPendingOutbox,
                totalPoisonedOutbox,
                totalPendingInbox,
                totalPoisonedInbox,
                _lastHeartbeat,
                _dbContexts.ConvertAll(d => d.DbContextName)
            );
        }

        public ServiceDetailDto ToDetailDto(TimeSpan threshold, DateTimeOffset now)
        {
            var activeInstances = _instances.Values.Count(i => now - i.LastHeartbeat <= threshold);
            var status = activeInstances > 0 ? "online" : "offline";

            var instanceDtos = _instances.Values.Select(i => new ServiceInstanceRecordDto(
                i.InstanceId,
                i.MachineName,
                i.Environment,
                i.StartedAt,
                i.LastHeartbeat,
                now - i.LastHeartbeat <= threshold
            )).ToList();

            return new ServiceDetailDto(
                ServiceName,
                status,
                instanceDtos,
                _dbContexts,
                _channels
            );
        }
    }

    private sealed class InstanceState(string instanceId)
    {
        public string InstanceId { get; } = instanceId;
        public string MachineName { get; private set; } = string.Empty;
        public string? Environment { get; private set; }
        public DateTimeOffset StartedAt { get; private set; }
        public DateTimeOffset LastHeartbeat { get; private set; }

        public void Update(ServiceHeartbeat hb)
        {
            MachineName = hb.MachineName;
            Environment = hb.Environment;
            StartedAt = hb.StartedAt;
            LastHeartbeat = hb.Timestamp;
        }
    }
}
