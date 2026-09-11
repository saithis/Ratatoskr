namespace Ratatoskr.Management.Contracts;

/// <summary>Provides management request/response communication without exposing a transport.</summary>
public interface IManagementClient
{
    Task<TResponse?> ExecuteAsync<TRequest, TResponse>(
        ManagementTarget target,
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Exposes the current, provider-maintained service discovery snapshot.</summary>
public interface IServiceCatalog
{
    event EventHandler<ServiceHeartbeatEventArgs>? ServiceUpdated;
    IReadOnlyList<ServiceCardDto> GetAllServices();
    ServiceDetailDto? GetService(string serviceName);
}

/// <summary>Dispatches a protocol request to registered management operations.</summary>
public interface IManagementCommandDispatcher
{
    Task<ManagementResponseEnvelope> DispatchAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default);
}

/// <summary>Hosts a management command dispatcher and service announcement source.</summary>
public interface IManagementCommandHost
{
    Task<ManagementResponseEnvelope> HandleAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default);

    Task<ServiceHeartbeat> GetAnnouncementAsync(CancellationToken cancellationToken = default);
}

/// <summary>Publishes provider-neutral management discovery events.</summary>
public interface IManagementEventPublisher
{
    ValueTask PublishAsync(ServiceHeartbeat announcement, CancellationToken cancellationToken = default);
}

/// <summary>Consumes provider-neutral management discovery events.</summary>
public interface IManagementEventSource
{
    event EventHandler<ServiceHeartbeatEventArgs>? AnnouncementReceived;
}

/// <summary>Event data for a service discovery announcement.</summary>
public sealed class ServiceHeartbeatEventArgs(ServiceHeartbeat heartbeat) : EventArgs
{
    public ServiceHeartbeat Heartbeat { get; } = heartbeat;
}

/// <summary>Contributes a named capability advertised by a management host.</summary>
public interface IManagementCapabilityContributor
{
    CapabilityDescriptor GetCapability();
}

/// <summary>Contributes provider-neutral topology information advertised by a management host.</summary>
public interface IManagementTopologyContributor
{
    IEnumerable<ChannelTopology> GetChannels();
}

/// <summary>Handles a strongly typed management operation.</summary>
public interface IManagementOperationHandler<TRequest, TResponse>
{
    string Operation { get; }
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

public sealed record ServiceCardDto(
    string ServiceName,
    string Status,
    int InstanceCount,
    long TotalPendingOutbox,
    long TotalPoisonedOutbox,
    long TotalPendingInbox,
    long TotalPoisonedInbox,
    DateTimeOffset LastHeartbeat,
    IReadOnlyList<string> DbContextNames);

public sealed record ServiceInstanceRecordDto(
    string InstanceId,
    string MachineName,
    string? Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeat,
    bool IsActive);

public sealed record ServiceDetailDto(
    string ServiceName,
    string Status,
    IReadOnlyList<ServiceInstanceRecordDto> Instances,
    IReadOnlyList<DbContextSummaryDto> DbContexts,
    IReadOnlyList<ChannelTopology> Channels);
