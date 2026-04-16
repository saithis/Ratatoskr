using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Ratatoskr.Endpoints;

/// <summary>
/// Succeeds any authorization requirement when the request was dispatched
/// in-process by the local backend proxy and targets a Ratatoskr management endpoint.
///
/// The path check is the critical security boundary: even if another component
/// accidentally attaches an <see cref="ILocalRatatoskrRequestFeature"/> to a request,
/// authorization is only bypassed for <c>/ratatoskr/api/v1/...</c> routes.
/// </summary>
internal sealed class LocalRatatoskrBypassAuthorizationHandler
    : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext httpContext)
            return Task.CompletedTask;

        if (httpContext.Features.Get<ILocalRatatoskrRequestFeature>() is null)
            return Task.CompletedTask;

        if (!IsManagementApiPath(httpContext.Request.Path))
            return Task.CompletedTask;

        foreach (var req in context.PendingRequirements.ToList())
            context.Succeed(req);

        return Task.CompletedTask;
    }

    private static bool IsManagementApiPath(PathString path) =>
        path.StartsWithSegments(ManagementApiEndpointExtensions.BasePath, StringComparison.OrdinalIgnoreCase);
}
