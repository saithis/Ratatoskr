using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Dispatches management protocol requests to feature-owned operation handlers.
/// </summary>
public sealed class ManagementRequestHandler(
    ManagementOperationDispatcher dispatcher,
    EfCoreManagementOperations efCoreOperations
)
{
    public Task<ManagementResponseEnvelope> HandleAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    ) => dispatcher.DispatchAsync(request, cancellationToken);

    /// <summary>
    /// Retained while the existing heartbeat publisher and in-process client are migrated
    /// to the transport-neutral host in the following pull request.
    /// </summary>
    public Task<ServiceHeartbeat> BuildHeartbeatAsync(CancellationToken cancellationToken = default) =>
        efCoreOperations.BuildHeartbeatAsync(cancellationToken);
}
