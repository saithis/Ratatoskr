using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ratatoskr.Management.Endpoints;

public static class ManagementApiEndpointExtensions
{
    /// <summary>
    /// Default base path under which all Ratatoskr management endpoints are mounted.
    /// Pass a custom <c>basePath</c> to <see cref="MapRatatoskrManagementApi"/> to override.
    /// </summary>
    private const string DefaultBasePath = "/ratatoskr/api/v1";

    /// <summary>
    /// Maps all registered Ratatoskr management endpoints under <paramref name="basePath"/>,
    /// applying <paramref name="policyName"/> authorization and disabling antiforgery.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrManagementApi(
        this IEndpointRouteBuilder endpoints,
        string policyName,
        string basePath = DefaultBasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

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

        var group = endpoints
            .MapGroup(basePath)
            .RequireAuthorization(policyName)
            .DisableAntiforgery()
            .WithMetadata(new RatatoskrManagementApiMetadata());

        foreach (var configurator in configurators)
            configurator.MapEndpoints(group);

        return endpoints;
    }
}
