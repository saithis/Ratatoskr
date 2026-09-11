using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Runtime;

/// <summary>Executes commands against the co-hosted management dispatcher.</summary>
public sealed class InProcessManagementCommandHost(
    IManagementCommandDispatcher dispatcher,
    EfCoreManagementOperations operations) : IManagementCommandHost
{
    public Task<ManagementResponseEnvelope> HandleAsync(ManagementRequestEnvelope request, CancellationToken cancellationToken = default) =>
        dispatcher.DispatchAsync(request, cancellationToken);

    public Task<ServiceHeartbeat> GetAnnouncementAsync(CancellationToken cancellationToken = default) =>
        operations.BuildHeartbeatAsync(cancellationToken);
}

/// <summary>In-memory discovery catalog for the in-process provider.</summary>
public sealed class InProcessServiceCatalog : IServiceCatalog, IManagementEventPublisher, IManagementEventSource
{
    private ServiceHeartbeat? _current;
    public event EventHandler<ServiceHeartbeatEventArgs>? ServiceUpdated;
    public event EventHandler<ServiceHeartbeatEventArgs>? AnnouncementReceived;

    public ValueTask PublishAsync(ServiceHeartbeat announcement, CancellationToken cancellationToken = default)
    {
        _current = announcement;
        var args = new ServiceHeartbeatEventArgs(announcement);
        ServiceUpdated?.Invoke(this, args);
        AnnouncementReceived?.Invoke(this, args);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<ServiceCardDto> GetAllServices() => _current is null ? [] : [ToCard(_current)];

    public ServiceDetailDto? GetService(string serviceName) =>
        _current is not null && string.Equals(_current.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)
            ? ToDetail(_current) : null;

    private static ServiceCardDto ToCard(ServiceHeartbeat heartbeat) => new(
        heartbeat.ServiceName, "online", 1,
        heartbeat.DbContexts.Sum(x => x.PendingOutboxCount), heartbeat.DbContexts.Sum(x => x.PoisonedOutboxCount),
        heartbeat.DbContexts.Sum(x => x.PendingInboxCount), heartbeat.DbContexts.Sum(x => x.PoisonedInboxCount), heartbeat.Timestamp,
        heartbeat.DbContexts.Select(x => x.DbContextName).ToArray());

    private static ServiceDetailDto ToDetail(ServiceHeartbeat heartbeat) => new(
        heartbeat.ServiceName, "online",
        [new ServiceInstanceRecordDto(heartbeat.InstanceId, heartbeat.MachineName, heartbeat.Environment, heartbeat.StartedAt, heartbeat.Timestamp, true)],
        heartbeat.DbContexts, heartbeat.Channels);
}

/// <summary>Transport-neutral client for a co-hosted management command host.</summary>
public sealed class InProcessManagementClient(
    IManagementCommandHost host,
    IOptions<RatatoskrManagementOptions> managementOptions,
    IOptions<ManagementRuntimeOptions> runtimeOptions) : IManagementClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TResponse?> ExecuteAsync<TRequest, TResponse>(ManagementTarget target, string operation, TRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!string.Equals(target.LogicalServiceName, managementOptions.Value.ServiceName, StringComparison.OrdinalIgnoreCase)
            || (target.InstanceId is not null && !string.Equals(target.InstanceId, managementOptions.Value.InstanceId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"The in-process provider cannot target service '{target.LogicalServiceName}'.");
        }

        var timeout = runtimeOptions.Value.RequestTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var envelope = new ManagementRequestEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = Guid.NewGuid().ToString("N"),
            OperationId = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Target = target,
            Deadline = DateTimeOffset.UtcNow.Add(timeout),
            Payload = new ManagementPayload(typeof(TRequest).FullName ?? typeof(TRequest).Name, JsonSerializer.Serialize(request, JsonOptions))
        };

        var response = await host.HandleAsync(envelope, linkedSource.Token);
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error?.SafeDetail ?? "Management operation failed.");
        }

        return string.IsNullOrEmpty(response.PayloadJson) ? default : JsonSerializer.Deserialize<TResponse>(response.PayloadJson, JsonOptions);
    }
}

internal sealed class InProcessManagementAnnouncementService(
    IManagementCommandHost host,
    IManagementEventPublisher publisher) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await publisher.PublishAsync(await host.GetAnnouncementAsync(cancellationToken), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
