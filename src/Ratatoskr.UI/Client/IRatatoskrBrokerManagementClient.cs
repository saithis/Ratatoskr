using Ratatoskr.Management.Contracts;

namespace Ratatoskr.UI.Client;

/// <summary>
/// Client for sending management queries and commands to services over the message broker or in-process.
/// </summary>
public interface IRatatoskrBrokerManagementClient
{
    ActiveServiceRegistry Registry { get; }

    Task<TResponse?> ExecuteAsync<TRequest, TResponse>(
        string serviceName,
        string? contextName,
        string action,
        TRequest request,
        CancellationToken cancellationToken = default
    );
}
