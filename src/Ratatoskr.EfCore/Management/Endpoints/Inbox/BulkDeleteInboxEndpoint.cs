using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record BulkDeleteInboxRequest(List<Guid>? Ids, bool? All);

internal static class BulkDeleteInboxEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapDelete("/poisoned", Handle);
    }

    private static async Task<Results<Ok, NotFound>> Handle(
        string contextName,
        [FromBody] BulkDeleteInboxRequest req,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        await provider.BulkDeleteInboxAsync(req.Ids, req.All is true, ct);
        return TypedResults.Ok();
    }
}
