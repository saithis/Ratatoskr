using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record BulkDeleteOutboxRequest(List<Guid>? Ids, bool? All);

internal static class BulkDeleteOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapDelete("/poisoned", Handle);
    }

    private static async Task<Results<Ok, NotFound>> Handle(
        string contextName,
        [FromBody] BulkDeleteOutboxRequest req,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        await provider.BulkDeleteOutboxAsync(req.Ids, req.All is true, ct);
        return TypedResults.Ok();
    }
}
