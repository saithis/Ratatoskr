using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record BulkRequeueOutboxRequest(List<Guid>? Ids, bool? All);

internal record BulkRequeueOutboxFailure(Guid Id, string Reason);

internal record BulkRequeueOutboxResponse(List<Guid> Succeeded, List<BulkRequeueOutboxFailure> Failed);

internal static class BulkRequeueOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapPost("/poisoned/requeue", Handle);
    }

    private static async Task<Results<Ok<BulkRequeueOutboxResponse>, NotFound>> Handle(
        string contextName,
        BulkRequeueOutboxRequest req,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        var result = await provider.BulkRequeueOutboxAsync(req.Ids, req.All is true, ct);
        return TypedResults.Ok(result);
    }
}
