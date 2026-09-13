using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Management.Http;

namespace Ratatoskr.Management;

/// <summary>Mounts the per-service management REST API.</summary>
public static class ManagementApiEndpointExtensions
{
    /// <summary>The path the management API is mounted at unless the caller says otherwise.</summary>
    public const string DefaultBasePath = "/ratatoskr/api/v1";

    /// <summary>
    /// Maps the per-service management REST API, guarded by a single authorization policy.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrManagementApi(
        this IEndpointRouteBuilder endpoints,
        string policyName,
        string basePath = DefaultBasePath
    ) => endpoints.MapRatatoskrManagementApi(ManagementApiPolicies.Single(policyName), basePath);

    /// <summary>
    /// Maps the per-service management REST API with separate policies for reading metadata,
    /// reading payloads, requeueing, deleting and bulk operations.
    /// </summary>
    /// <remarks>
    /// This is a supported, documented public API in its own right, not just the dashboard's
    /// backend: it is how a script, a runbook or another service drives a Ratatoskr host directly.
    /// It is built from the same route tree as the dashboard facade, so the two always expose the
    /// same operations.
    /// </remarks>
    public static IEndpointRouteBuilder MapRatatoskrManagementApi(
        this IEndpointRouteBuilder endpoints,
        ManagementApiPolicies policies,
        string basePath = DefaultBasePath
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        policies.ValidateRegistered(endpoints);

        var strategy =
            endpoints.ServiceProvider.GetRequiredService<LocalManagementDispatchStrategy>();

        var group = endpoints
            .MapGroup(basePath.TrimEnd('/'))
            .RequireAuthorization(policies.ViewMetadata)
            .WithTags("Ratatoskr management");

        group.AddEndpointFilter(
            async (context, next) =>
            {
                context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
                return await next(context);
            }
        );

        ManagementApiRoutes.Map(group, strategy, policies);
        return endpoints;
    }
}
