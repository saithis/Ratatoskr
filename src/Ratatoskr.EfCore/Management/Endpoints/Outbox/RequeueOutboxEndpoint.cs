using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal static class RequeueOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapPost("/poisoned/{id:guid}/requeue", Handle);
    }

    private static async Task<Results<Ok, NotFound, BadRequest<string>, Conflict>> Handle(
        string contextName,
        Guid id,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        var outcome = await provider.RequeueOutboxAsync(id, ct);
        if (outcome == SingleRequeueOutcome.Success) return TypedResults.Ok();
        if (outcome == SingleRequeueOutcome.NotFound) return TypedResults.NotFound();
        if (outcome == SingleRequeueOutcome.NotPoisoned) return TypedResults.BadRequest("Message is not poisoned.");
        return TypedResults.Conflict();
    }
}
