using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;

namespace Ratatoskr.Management.Http;

/// <summary>What the dashboard needs to know to talk to the services it watches.</summary>
public sealed class ManagementDashboardOptions
{
    /// <summary>How long the dashboard waits for a service to answer before giving up.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after its last announcement a replica is shown as stale rather than online.
    /// Defaults to three heartbeat intervals so one missed heartbeat is not an alarm.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How long audit entries are kept before the retention worker removes them.</summary>
    public TimeSpan AuditRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often the audit retention worker runs.</summary>
    public TimeSpan AuditCleanupInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long persisted service instance snapshots are kept before being pruned.</summary>
    public TimeSpan SnapshotRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
/// Dispatches the dashboard's REST facade through a named transport to a remote service.
/// </summary>
/// <remarks>
/// Routes carry <c>{transport}</c> and <c>{serviceName}</c>; an optional <c>instanceId</c> query
/// parameter narrows to one replica. Everything below this class — the routes, the DTOs, the error
/// mapping — is shared with the per-service API, so the dashboard cannot offer an operation a
/// service does not implement, or render its answer differently.
/// </remarks>
internal sealed class TransportManagementDispatchStrategy(
    IManagementTransportRegistry transports,
    ServiceRegistry registry,
    IOptions<ManagementDashboardOptions> options,
    TimeProvider timeProvider
) : IManagementDispatchStrategy
{
    public async Task<ManagementResponseEnvelope> ExecuteAsync(
        HttpContext http,
        string operation,
        object request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        var transportName = ManagementHttp.RouteValue(http, "transport") ?? string.Empty;
        var serviceName = ManagementHttp.RouteValue(http, "serviceName") ?? string.Empty;
        var instanceId = http.Request.Query["instanceId"].FirstOrDefault();

        var envelope = new ManagementRequestEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = http.TraceIdentifier,
            OperationId = ManagementHttp.ResolveOperationId(http),
            Target = new ManagementTarget(
                serviceName,
                string.IsNullOrWhiteSpace(instanceId) ? null : instanceId,
                ManagementHttp.RouteValue(http, "contextName")
            ),
            Operation = operation,
            Deadline = timeProvider.GetUtcNow().Add(options.Value.RequestTimeout),
            Actor = ManagementHttp.ActorFrom(http),
            Payload = ManagementJson.ToElement(request, request.GetType()),
        };

        if (!transports.TryGet(transportName, out var transport))
        {
            return ManagementResponseEnvelope.Failed(
                envelope,
                ManagementResultStatus.NotFound,
                new ManagementError(
                    ManagementErrorCodes.NotFound,
                    $"No management transport named '{transportName}' is configured."
                )
            );
        }

        var address = registry.GetAddress(transportName, serviceName, envelope.Target.InstanceId);
        if (address is null)
        {
            // Never announced, or announced and then pruned. Either way there is no address to
            // send to, and waiting out the deadline would tell the operator nothing useful.
            return ManagementResponseEnvelope.Failed(
                envelope,
                ManagementResultStatus.NotFound,
                new ManagementError(
                    ManagementErrorCodes.TargetUnreachable,
                    envelope.Target.InstanceId is null
                        ? $"Service '{serviceName}' has not been seen on transport '{transportName}'."
                        : $"Instance '{envelope.Target.InstanceId}' of service '{serviceName}' has not been seen on transport '{transportName}'."
                )
            );
        }

        if (
            envelope.Target.InstanceId is not null
            && !transport.Capabilities.HasFlag(ManagementTransportCapabilities.InstanceTargeting)
        )
        {
            return ManagementResponseEnvelope.Failed(
                envelope,
                ManagementResultStatus.Unsupported,
                new ManagementError(
                    ManagementErrorCodes.UnsupportedCapability,
                    $"Transport '{transportName}' cannot address a single replica."
                )
            );
        }

        return await transport.SendAsync(address, envelope, cancellationToken);
    }
}
