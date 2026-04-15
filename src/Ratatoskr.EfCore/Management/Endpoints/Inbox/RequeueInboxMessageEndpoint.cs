using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record RequeueInboxMessageResponse(List<Guid> RequeuedHandlerStatusIds);

internal static class RequeueInboxMessageEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapPost("/messages/{messageId}/requeue", Handle);
    }

    private static async Task<Results<Ok<RequeueInboxMessageResponse>, NotFound, Conflict>> Handle(
        string contextName,
        string messageId,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        var outcome = await provider.RequeueAllInboxHandlersForMessageAsync(messageId, ct);
        if (!outcome.Found) return TypedResults.NotFound();
        if (outcome.Conflict) return TypedResults.Conflict();
        return TypedResults.Ok(new RequeueInboxMessageResponse(outcome.RequeuedIds.ToList()));
    }
}
