using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Http;

/// <summary>
/// Dispatches the per-service REST API straight into this process's own operations. No broker,
/// no discovery — but the very same envelope, dispatcher and operations a remote caller reaches,
/// so the direct API cannot behave differently from the dashboard's view of the same service.
/// </summary>
internal sealed class LocalManagementDispatchStrategy(
    IManagementDispatcher dispatcher,
    IOptions<ManagementAgentOptions> options,
    TimeProvider timeProvider
) : IManagementDispatchStrategy
{
    public Task<ManagementResponseEnvelope> ExecuteAsync(
        HttpContext http,
        string operation,
        object request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);

        var agent = options.Value;
        var envelope = new ManagementRequestEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = http.TraceIdentifier,
            OperationId = ManagementHttp.ResolveOperationId(http),
            Target = new ManagementTarget(
                agent.ServiceName,
                agent.InstanceId,
                ManagementHttp.RouteValue(http, "contextName")
            ),
            Operation = operation,
            Deadline = timeProvider.GetUtcNow().Add(agent.RequestTimeout),
            Actor = ManagementHttp.ActorFrom(http),
            Payload = ManagementJson.ToElement(request, request.GetType()),
        };

        return dispatcher.DispatchAsync(envelope, cancellationToken);
    }
}
