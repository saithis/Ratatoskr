using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Sends management requests the way the dashboard's REST facade does, without an HTTP hop.
/// </summary>
/// <remarks>
/// Tests that exercise a transport want the envelope, the address lookup and the correlation, not
/// ASP.NET routing. This does exactly what <c>TransportManagementDispatchStrategy</c> does, minus
/// the <c>HttpContext</c>.
/// </remarks>
public sealed class ManagementTestClient(IServiceProvider services)
{
    private readonly IManagementTransportRegistry _transports =
        services.GetRequiredService<IManagementTransportRegistry>();
    private readonly ServiceRegistry _registry = services.GetRequiredService<ServiceRegistry>();
    private readonly TimeProvider _time = services.GetRequiredService<TimeProvider>();

    /// <summary>The dashboard's view of what it has discovered.</summary>
    public ServiceRegistry Registry => _registry;

    /// <summary>Waits until a service has announced itself on a transport, or fails the test.</summary>
    public async Task<ServiceDetail> WaitForServiceAsync(
        string transportName,
        string serviceName,
        TimeSpan? timeout = null
    )
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (_registry.GetService(transportName, serviceName) is { } detail)
            {
                return detail;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Service '{serviceName}' was not discovered on transport '{transportName}'. "
                + $"Known services: {string.Join(", ", _registry.GetServices().Select(card => $"{card.TransportName}/{card.ServiceName}"))}."
        );
    }

    /// <summary>Sends one request and returns the raw response envelope.</summary>
    public async Task<ManagementResponseEnvelope> SendAsync(
        string transportName,
        string serviceName,
        string operation,
        object payload,
        string? resource = null,
        string? instanceId = null,
        Guid? operationId = null,
        TimeSpan? timeout = null
    )
    {
        var envelope = new ManagementRequestEnvelope
        {
            ProtocolVersion = ManagementProtocol.Current,
            RequestId = Guid.NewGuid().ToString("N"),
            OperationId = operationId ?? Guid.NewGuid(),
            Target = new ManagementTarget(serviceName, instanceId, resource),
            Operation = operation,
            Deadline = _time.GetUtcNow().Add(timeout ?? TimeSpan.FromSeconds(30)),
            Actor = new ManagementActor("test-operator", "Test Operator", "Test"),
            Payload = ManagementJson.ToElement(payload, payload.GetType()),
        };

        var address =
            _registry.GetAddress(transportName, serviceName, instanceId)
            ?? ManagementAddress.Empty;

        return await _transports.Get(transportName).SendAsync(address, envelope);
    }

    /// <summary>Sends one request and deserializes a successful response, failing otherwise.</summary>
    public async Task<TResponse> ExecuteAsync<TResponse>(
        string transportName,
        string serviceName,
        string operation,
        object payload,
        string? resource = null,
        string? instanceId = null,
        Guid? operationId = null,
        TimeSpan? timeout = null
    )
    {
        var response = await SendAsync(
            transportName,
            serviceName,
            operation,
            payload,
            resource,
            instanceId,
            operationId,
            timeout
        );

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(
                $"'{operation}' failed: {response.Error?.Code} — {response.Error?.Detail}"
            );
        }

        return ManagementJson.FromElement<TResponse>(response.Payload)
            ?? throw new InvalidOperationException($"'{operation}' returned no payload.");
    }
}
