using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal static class DeleteInboxHandlerEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapDelete("/poisoned/{handlerStatusId:guid}", Handle);
    }

    private static async Task<Results<Ok, NotFound, BadRequest<string>, Conflict>> Handle(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        var outcome = await provider.DeleteInboxHandlerStatusAsync(handlerStatusId, ct);
        if (outcome == SingleDeleteOutcome.Success) return TypedResults.Ok();
        if (outcome == SingleDeleteOutcome.NotFound) return TypedResults.NotFound();
        if (outcome == SingleDeleteOutcome.NotPoisoned) return TypedResults.BadRequest("Handler status is not poisoned.");
        return TypedResults.Conflict();
    }
}
