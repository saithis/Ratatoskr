using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ratatoskr.Endpoints;

public static class ManagementApiEndpointExtensions
{
    /// <summary>
    /// Base path prefix under which all Ratatoskr management endpoints are mounted.
    /// Used by the in-process authorization bypass to scope the bypass to only
    /// management routes.
    /// </summary>
    internal const string BasePath = "/ratatoskr/api/v1";

    public static IEndpointRouteBuilder MapRatatoskrManagementApi(
        this IEndpointRouteBuilder endpoints,
        string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var configurators = endpoints.ServiceProvider
            .GetServices<IRatatoskrEndpointConfigurator>()
            .ToList();

        // No transport registered any management endpoints — nothing to map.
        // Safe for hosts that conditionally include Ratatoskr durability.
        if (configurators.Count == 0) return endpoints;

        // Validate the policy exists at startup rather than at first request.
        var authOptions = endpoints.ServiceProvider
            .GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        if (authOptions.GetPolicy(policyName) is null)
            throw new InvalidOperationException(
                $"Authorization policy '{policyName}' is not registered. " +
                "Call services.AddAuthorization() and define the policy before calling MapRatatoskrManagementApi.");

        foreach (var configurator in configurators)
            configurator.MapEndpoints(endpoints, policyName);

        return endpoints;
    }
}
