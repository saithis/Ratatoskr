using System.Collections.Concurrent;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Registry;

/// <summary>Identifies one replica across every transport the dashboard watches.</summary>
public sealed record ServiceInstanceKey(string TransportName, string ServiceName, string InstanceId);

/// <summary>How fresh a replica's last announcement is.</summary>
public enum ServiceLiveness
{
    /// <summary>Announced within the staleness window.</summary>
    Online = 0,

    /// <summary>Last seen longer ago than the staleness window. Still shown, marked by age.</summary>
    Stale = 1,
}

/// <summary>One replica, as the dashboard renders it.</summary>
public sealed record ServiceInstanceRecord(
    string InstanceId,
    string MachineName,
    string? Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    ServiceLiveness Liveness
);

/// <summary>One logical service on one transport, summarised for the service list.</summary>
public sealed record ServiceCard(
    string TransportName,
    string ServiceName,
    ServiceLiveness Liveness,
    int InstanceCount,
    int OnlineInstanceCount,
    long PendingOutbox,
    long PoisonedOutbox,
    long PendingInbox,
    long PoisonedInbox,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<string> ContextNames,
    IReadOnlyList<CapabilityDescriptor> Capabilities
);

/// <summary>One logical service on one transport, in full.</summary>
public sealed record ServiceDetail(
    string TransportName,
    string ServiceName,
    ServiceLiveness Liveness,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<ServiceInstanceRecord> Instances,
    IReadOnlyList<DbContextSummary> DbContexts,
    IReadOnlyList<ChannelTopology> Channels,
    IReadOnlyList<CapabilityDescriptor> Capabilities
);

/// <summary>Carries the announcement that caused a registry change.</summary>
public sealed class ServiceAnnouncementEventArgs(string transportName, ServiceAnnouncement announcement)
    : EventArgs
{
    /// <summary>The transport the announcement arrived on.</summary>
    public string TransportName { get; } = transportName;

    /// <summary>The announcement.</summary>
    public ServiceAnnouncement Announcement { get; } = announcement;
}

/// <summary>
/// An in-memory view of every replica the dashboard has heard from, across every transport.
/// </summary>
/// <remarks>
/// Service identity is <c>(transport, service, instance)</c>, which is what makes two RabbitMQ
/// brokers — or a broker plus the in-process transport — usable at once, and what makes a
/// migration between them legible: the same service name appears under both transports until the
/// old one goes quiet.
/// <para>
/// Nothing is pruned here. A replica that stopped announcing is kept and marked
/// <see cref="ServiceLiveness.Stale"/>, because "the orders service vanished from the dashboard"
/// is a far worse answer to an outage than "the orders service was last seen 4 minutes ago".
/// Bounded retention belongs to the persistent store behind this cache.
/// </para>
/// </remarks>
public sealed class ServiceRegistry(TimeProvider timeProvider, TimeSpan staleAfter)
{
    private readonly ConcurrentDictionary<ServiceInstanceKey, ServiceAnnouncement> _instances =
        new();

    /// <summary>Raised when an announcement changed the registry.</summary>
    public event EventHandler<ServiceAnnouncementEventArgs>? ServiceUpdated;

    /// <summary>How long after its last announcement a replica is considered stale.</summary>
    public TimeSpan StaleAfter { get; } = staleAfter;

    /// <summary>
    /// Records an announcement, unless it is older than the one already held for that replica.
    /// Returns whether the registry changed.
    /// </summary>
    /// <remarks>
    /// Out-of-order delivery is normal: a heartbeat can overtake its predecessor on a different
    /// channel, and a fanout redelivery after a reconnect can resurrect an old one. Rejecting
    /// anything not strictly newer means state never moves backwards.
    /// </remarks>
    public bool Publish(string transportName, ServiceAnnouncement announcement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        ArgumentNullException.ThrowIfNull(announcement);

        var snapshot = Freeze(announcement);
        var key = new ServiceInstanceKey(
            transportName,
            snapshot.ServiceName,
            snapshot.InstanceId
        );

        while (true)
        {
            if (_instances.TryGetValue(key, out var existing))
            {
                if (existing.AnnouncedAt >= snapshot.AnnouncedAt)
                {
                    return false;
                }

                if (!_instances.TryUpdate(key, snapshot, existing))
                {
                    continue;
                }
            }
            else if (!_instances.TryAdd(key, snapshot))
            {
                continue;
            }

            ServiceUpdated?.Invoke(this, new ServiceAnnouncementEventArgs(transportName, snapshot));
            return true;
        }
    }

    /// <summary>Removes every replica of a transport, used when that transport disconnects.</summary>
    public void Clear(string transportName)
    {
        foreach (var key in _instances.Keys.Where(k => k.TransportName == transportName))
        {
            _instances.TryRemove(key, out _);
        }
    }

    /// <summary>Every known service, newest-seen state first computed against the current clock.</summary>
    public IReadOnlyList<ServiceCard> GetServices()
    {
        var now = timeProvider.GetUtcNow();
        return
        [
            .. _instances
                .GroupBy(pair => (pair.Key.TransportName, pair.Key.ServiceName))
                .Select(group => ToCard(group.Key.TransportName, group.Key.ServiceName, [.. group.Select(pair => pair.Value)], now))
                .OrderBy(card => card.TransportName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(card => card.ServiceName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>One service in full, or null when it has never been seen.</summary>
    public ServiceDetail? GetService(string transportName, string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var instances = _instances
            .Where(pair =>
                string.Equals(pair.Key.TransportName, transportName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Key.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)
            )
            .Select(pair => pair.Value)
            .ToArray();

        return instances.Length == 0 ? null : ToDetail(transportName, serviceName, instances, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// The transport address to send a command to.
    /// </summary>
    /// <remarks>
    /// For a logical-service target this returns any replica's address, because every replica of a
    /// service advertises the same command exchange and the same <c>svc.</c> routing key. That is
    /// deliberate: picking "whichever replica announced last" would send traffic to a dead queue
    /// until the next heartbeat arrived.
    /// </remarks>
    public ManagementAddress? GetAddress(string transportName, string serviceName, string? instanceId)
    {
        if (instanceId is not null)
        {
            return _instances.TryGetValue(
                new ServiceInstanceKey(transportName, serviceName, instanceId),
                out var instance
            )
                ? instance.Address
                : null;
        }

        return _instances
            .Where(pair =>
                string.Equals(pair.Key.TransportName, transportName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Key.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)
            )
            .OrderByDescending(pair => pair.Value.AnnouncedAt)
            .Select(pair => pair.Value.Address)
            .FirstOrDefault();
    }

    private ServiceCard ToCard(
        string transportName,
        string serviceName,
        IReadOnlyList<ServiceAnnouncement> instances,
        DateTimeOffset now
    )
    {
        var online = instances.Where(instance => IsOnline(instance, now)).ToArray();

        // Counts are instance-local, so a service's backlog is the sum over its live replicas.
        // When every replica has gone quiet the last known numbers are shown rather than zero,
        // because zero would read as "the backlog drained" when it means "nobody is reporting".
        var counted = online.Length > 0 ? online : instances;

        return new ServiceCard(
            transportName,
            serviceName,
            online.Length > 0 ? ServiceLiveness.Online : ServiceLiveness.Stale,
            instances.Count,
            online.Length,
            counted.Sum(instance => instance.DbContexts.Sum(context => context.PendingOutbox)),
            counted.Sum(instance => instance.DbContexts.Sum(context => context.PoisonedOutbox)),
            counted.Sum(instance => instance.DbContexts.Sum(context => context.PendingInbox)),
            counted.Sum(instance => instance.DbContexts.Sum(context => context.PoisonedInbox)),
            instances.Max(instance => instance.AnnouncedAt),
            [
                .. counted
                    .SelectMany(instance => instance.DbContexts)
                    .Select(context => context.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase),
            ],
            Newest(instances).Capabilities
        );
    }

    private ServiceDetail ToDetail(
        string transportName,
        string serviceName,
        IReadOnlyList<ServiceAnnouncement> instances,
        DateTimeOffset now
    )
    {
        var newest = Newest(instances);
        return new ServiceDetail(
            transportName,
            serviceName,
            instances.Any(instance => IsOnline(instance, now))
                ? ServiceLiveness.Online
                : ServiceLiveness.Stale,
            newest.AnnouncedAt,
            [
                .. instances
                    .OrderBy(instance => instance.InstanceId, StringComparer.Ordinal)
                    .Select(instance => new ServiceInstanceRecord(
                        instance.InstanceId,
                        instance.MachineName,
                        instance.Environment,
                        instance.StartedAt,
                        instance.AnnouncedAt,
                        IsOnline(instance, now) ? ServiceLiveness.Online : ServiceLiveness.Stale
                    )),
            ],
            newest.DbContexts,
            newest.Channels,
            newest.Capabilities
        );
    }

    private bool IsOnline(ServiceAnnouncement announcement, DateTimeOffset now) =>
        announcement.AnnouncedAt >= now - StaleAfter;

    private static ServiceAnnouncement Newest(IReadOnlyList<ServiceAnnouncement> instances) =>
        instances.MaxBy(instance => instance.AnnouncedAt)!;

    /// <summary>
    /// Deep-copies the mutable collections off an announcement. The registry hands snapshots to
    /// SSE writers and HTTP handlers on other threads, and a transport is free to reuse the
    /// object it deserialized into.
    /// </summary>
    private static ServiceAnnouncement Freeze(ServiceAnnouncement source) =>
        source with
        {
            Capabilities = [.. source.Capabilities],
            DbContexts = [.. source.DbContexts],
            Channels =
            [
                .. source.Channels.Select(channel => channel with
                {
                    MessageTypes = [.. channel.MessageTypes],
                    TransportBindings =
                    [
                        .. channel.TransportBindings.Select(binding => binding with
                        {
                            Properties = new Dictionary<string, string>(
                                binding.Properties,
                                StringComparer.Ordinal
                            ),
                        }),
                    ],
                }),
            ],
        };
}
