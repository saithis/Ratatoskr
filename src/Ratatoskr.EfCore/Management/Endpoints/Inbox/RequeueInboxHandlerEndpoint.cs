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

    private static async Task<Results<Ok, NotFound, BadRequest<string>, Conflict>> Handle(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var outcome = await RequeueHelper.RequeueInboxHandlerAsync(db, handlerStatusId, ct);
        if (outcome == SingleRequeueOutcome.Success) return TypedResults.Ok();
        if (outcome == SingleRequeueOutcome.NotFound) return TypedResults.NotFound();
        if (outcome == SingleRequeueOutcome.NotPoisoned) return TypedResults.BadRequest("Handler status is not poisoned.");
        return TypedResults.Conflict();
    }
}
