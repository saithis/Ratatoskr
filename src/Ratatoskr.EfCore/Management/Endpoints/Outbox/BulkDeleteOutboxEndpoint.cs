using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static class BulkDeleteOutboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        // Two distinct URLs so that "delete all" cannot be requested by accident when a
        // client forgets to attach a body. Some HTTP intermediaries also strip request
        // bodies on DELETE, which would silently convert "delete these 5 ids" into
        // "delete everything" if the two operations shared a route.
        outboxGroup.MapDelete("/poisoned", HandleByIdsAsync);
        outboxGroup.MapDelete("/poisoned/all", HandleAllAsync);
    }

    private static async Task<
        Results<Ok<BulkDeleteOutboxResponse>, ProblemHttpResult>
    > HandleByIdsAsync(
        string contextName,
        [FromBody] BulkDeleteOutboxRequest req,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureOutbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        if (!BulkRequestValidator.TryValidateIds(req.Ids, out var error))
        {
            return ManagementResults.BadRequest(error!);
        }

        var deletedCount = await db.Set<OutboxMessageEntity>()
            .Where(x => req.Ids!.Contains(x.Id) && x.IsPoisoned)
            .ExecuteDeleteAsync(ct);

        return TypedResults.Ok(new BulkDeleteOutboxResponse(deletedCount));
    }

    private static async Task<Results<Ok, ProblemHttpResult>> HandleAllAsync(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureOutbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        await db.Set<OutboxMessageEntity>().Where(x => x.IsPoisoned).ExecuteDeleteAsync(ct);

        return TypedResults.Ok();
    }

    internal record BulkDeleteOutboxRequest(List<Guid>? Ids);

    internal record BulkDeleteOutboxResponse(int DeletedCount);
}
