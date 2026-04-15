using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Ratatoskr.Endpoints;

/// <summary>
/// Succeeds any authorization requirement when the request was dispatched
/// in-process by the local backend proxy (ILocalRatatoskrRequestFeature present).
/// Registered as singleton during AddRatatoskr().
/// </summary>
internal sealed class LocalRatatoskrBypassAuthorizationHandler
    : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        var httpContext = context.Resource as HttpContext;

        if (httpContext?.Features.Get<ILocalRatatoskrRequestFeature>() is not null)
            foreach (var req in context.PendingRequirements.ToList())
                context.Succeed(req);

        return Task.CompletedTask;
    }
}
