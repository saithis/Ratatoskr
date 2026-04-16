using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.EfCore.Management;

internal static class RequeueInboxHandlerEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapPost("/poisoned/{handlerStatusId:guid}/requeue", Handle);
    }

    private static async Task<Results<Ok, ProblemHttpResult>> Handle(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox)
            return ManagementResults.NotFound($"No inbox is registered for DbContext '{contextName}'.");

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var outcome = await RequeueHelper.RequeueInboxHandlerAsync(db, handlerStatusId, ct);
        return outcome switch
        {
            SingleRequeueOutcome.Success => TypedResults.Ok(),
            SingleRequeueOutcome.NotFound => ManagementResults.NotFound($"Inbox handler status '{handlerStatusId}' was not found."),
            SingleRequeueOutcome.NotPoisoned => ManagementResults.BadRequest("Handler status is not poisoned."),
            _ => ManagementResults.Conflict("Handler status was modified by another operation; retry."),
        };
    }
}
