using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Transports;

/// <summary>
/// Reaches a service that is co-hosted in this very process. No broker, no serialization hop —
/// but the same envelope, dispatcher and operations a remote caller goes through, so a dashboard
/// embedded in the service it manages behaves exactly like a remote one.
/// </summary>
internal sealed class InProcessManagementTransport(
    string name,
    IManagementDispatcher dispatcher,
    IOptions<ManagementAgentOptions> options
) : IManagementTransport
{
    public string Name { get; } = name;

    public ManagementTransportCapabilities Capabilities =>
        ManagementTransportCapabilities.LogicalServiceTargeting
        | ManagementTransportCapabilities.InstanceTargeting
        | ManagementTransportCapabilities.Discovery;

    public Task<ManagementResponseEnvelope> SendAsync(
        ManagementAddress address,
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = options.Value;
        var addressesThisProcess =
            string.Equals(request.Target.ServiceName, agent.ServiceName, StringComparison.OrdinalIgnoreCase)
            && (
                request.Target.InstanceId is null
                || string.Equals(request.Target.InstanceId, agent.InstanceId, StringComparison.Ordinal)
            );

        if (!addressesThisProcess)
        {
            // Failing fast here matters: a dashboard with several transports will otherwise wait
            // out the deadline against a transport that could never have reached the target.
            return Task.FromResult(
                ManagementResponseEnvelope.Failed(
                    request,
                    ManagementResultStatus.NotFound,
                    new ManagementError(
                        ManagementErrorCodes.TargetUnreachable,
                        $"The in-process transport hosts '{agent.ServiceName}' ({agent.InstanceId}) and cannot reach "
                            + $"'{request.Target.ServiceName}'"
                            + (request.Target.InstanceId is null ? "." : $" ({request.Target.InstanceId}).")
                    )
                )
            );
        }

        return dispatcher.DispatchAsync(request, cancellationToken);
    }
}

/// <summary>
/// Announces the co-hosted service on a timer.
/// </summary>
/// <remarks>
/// The loop is the point. Announcing once at startup leaves a dashboard showing the service until
/// the staleness window elapses and then showing nothing at all, which looks exactly like an
/// outage — for a service running in the same process as the dashboard rendering it.
/// <para>
/// The timer runs on the injected <see cref="TimeProvider"/>, so a test can advance past several
/// intervals instantly instead of sleeping through them.
/// </para>
/// </remarks>
internal sealed class InProcessManagementDiscoverySource(
    string transportName,
    ManagementAgent agent,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
) : IManagementDiscoverySource
{
    public string TransportName { get; } = transportName;

    public async IAsyncEnumerable<ServiceAnnouncement> ListenAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Announce immediately so a dashboard that just started does not sit empty for a whole
        // heartbeat interval before showing a service running beside it.
        yield return await agent.DescribeAsync(cancellationToken);

        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            yield return await agent.DescribeAsync(cancellationToken);
        }
    }
}
