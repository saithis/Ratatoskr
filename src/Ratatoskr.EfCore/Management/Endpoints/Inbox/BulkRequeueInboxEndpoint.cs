using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record BulkRequeueInboxRequest(List<Guid>? Ids, bool? All);

internal record BulkRequeueInboxFailure(Guid Id, string Reason);

internal record BulkRequeueInboxResponse(List<Guid> Succeeded, List<BulkRequeueInboxFailure> Failed);

internal static class BulkRequeueInboxEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapPost("/poisoned/requeue", Handle);
    }

    private static async Task<Results<Ok<BulkRequeueInboxResponse>, NotFound>> Handle(
        string contextName,
        BulkRequeueInboxRequest req,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        var result = await provider.BulkRequeueInboxAsync(req.Ids, req.All is true, ct);
        return TypedResults.Ok(result);
    }
}
