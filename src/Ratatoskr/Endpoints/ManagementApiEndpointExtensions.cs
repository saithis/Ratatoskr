using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ratatoskr.Endpoints;

public static class ManagementApiEndpointExtensions
{
    public static IEndpointRouteBuilder MapRatatoskrManagementApi(
        this IEndpointRouteBuilder endpoints,
        string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        // Validate the policy exists at startup rather than at first request.
        var authOptions = endpoints.ServiceProvider
            .GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        if (authOptions.GetPolicy(policyName) is null)
            throw new InvalidOperationException(
                $"Authorization policy '{policyName}' is not registered. " +
                "Call services.AddAuthorization() and define the policy before calling MapRatatoskrManagementApi.");

        var configurators = endpoints.ServiceProvider
            .GetServices<IRatatoskrEndpointConfigurator>();

        foreach (var configurator in configurators)
            configurator.MapEndpoints(endpoints, policyName);

        return endpoints;
    }
}
