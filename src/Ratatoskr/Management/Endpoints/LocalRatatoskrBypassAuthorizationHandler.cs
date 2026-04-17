using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Ratatoskr.Endpoints;

namespace Ratatoskr.Management.Endpoints;

/// <summary>
/// Succeeds any authorization requirement when the request was dispatched
/// in-process by the local backend proxy and targets a Ratatoskr management endpoint.
///
/// The endpoint metadata check is the critical security boundary: even if another
/// component accidentally attaches an <see cref="ILocalRatatoskrRequestFeature"/> to a
/// request, authorization is only bypassed for endpoints that carry
/// <see cref="RatatoskrManagementApiMetadata"/>. This also means the bypass works
/// correctly regardless of any URL prefix configured by the caller.
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

        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<RatatoskrManagementApiMetadata>() is null)
            return Task.CompletedTask;

        foreach (var req in context.PendingRequirements.ToList())
            context.Succeed(req);

        return Task.CompletedTask;
    }
}
