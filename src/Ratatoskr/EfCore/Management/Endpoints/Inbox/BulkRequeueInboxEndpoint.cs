using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class BulkRequeueInboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapPost("/poisoned/requeue", HandleByIdsAsync);
        inboxGroup.MapPost("/poisoned/requeue/all", HandleAllAsync);
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
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        return await BulkRequeue.ByIdsAsync<InboxHandlerStatusEntity>(db, req, ct);
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
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        return await BulkRequeue.AllAsync<InboxHandlerStatusEntity>(db, ct);
    }
}
