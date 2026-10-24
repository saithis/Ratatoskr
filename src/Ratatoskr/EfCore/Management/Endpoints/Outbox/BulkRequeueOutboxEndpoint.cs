using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static class BulkRequeueOutboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        outboxGroup.MapPost("/poisoned/requeue", HandleByIdsAsync);
        outboxGroup.MapPost("/poisoned/requeue/all", HandleAllAsync);
    }

    private static async Task<
        Results<Ok<BulkRequeueResponse>, ProblemHttpResult>
    > HandleByIdsAsync(
        string contextName,
        BulkRequeueRequest req,
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

        return await BulkRequeue.ByIdsAsync<OutboxMessageEntity>(db, req, ct);
    }

    private static async Task<
        Results<Ok<BulkRequeueResponse>, ProblemHttpResult>
    > HandleAllAsync(
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

        return await BulkRequeue.AllAsync<OutboxMessageEntity>(db, ct);
    }
}
