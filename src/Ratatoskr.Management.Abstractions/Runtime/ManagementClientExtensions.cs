namespace Ratatoskr.Management.Contracts;

/// <summary>Convenience overloads for clients used by HTTP and UI adapters.</summary>
public static class ManagementClientExtensions
{
    public static Task<TResponse?> ExecuteAsync<TRequest, TResponse>(
        this IManagementClient client,
        string serviceName,
        string? contextName,
        string operation,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.ExecuteAsync<TRequest, TResponse>(
            new ManagementTarget(serviceName, ResourceId: contextName), operation, request, cancellationToken);
    }
}
