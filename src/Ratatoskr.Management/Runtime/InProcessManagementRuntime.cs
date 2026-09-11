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
    private readonly ServiceRegistry _registry;

    public InProcessServiceCatalog(TimeProvider timeProvider)
    {
        _registry = new ServiceRegistry(timeProvider, TimeSpan.FromSeconds(45));
    }
    public event EventHandler<ServiceHeartbeatEventArgs>? ServiceUpdated
    {
        add => _registry.ServiceUpdated += value;
        remove => _registry.ServiceUpdated -= value;
    }
    public event EventHandler<ServiceHeartbeatEventArgs>? AnnouncementReceived;

    public ValueTask PublishAsync(ServiceHeartbeat announcement, CancellationToken cancellationToken = default)
    {
        if (_registry.Publish(announcement))
        {
            AnnouncementReceived?.Invoke(this, new ServiceHeartbeatEventArgs(announcement));
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<ServiceCardDto> GetAllServices() => _registry.GetAllServices();
    public ServiceDetailDto? GetService(string serviceName) => _registry.GetService(serviceName);
}

/// <summary>Transport-neutral client for a co-hosted management command host.</summary>
public sealed class InProcessManagementClient(
    IManagementCommandHost host,
    IOptions<RatatoskrManagementOptions> managementOptions,
    IOptions<ManagementRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider) : IManagementClient
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
            Deadline = timeProvider.GetUtcNow().Add(timeout),
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
