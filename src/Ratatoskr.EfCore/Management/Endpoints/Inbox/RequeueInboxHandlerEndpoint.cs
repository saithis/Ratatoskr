using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class RequeueInboxHandlerEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapPost("/poisoned/{handlerStatusId:guid}/requeue", HandleAsync);
    }

    private static async Task<Results<Ok, ProblemHttpResult>> HandleAsync(
        string contextName,
        Guid handlerStatusId,
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

        var entity = await db.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == handlerStatusId, ct);

        if (entity is null)
        {
            return ManagementResults.NotFound(
                $"Inbox handler status '{handlerStatusId}' was not found."
            );
        }

        if (!entity.IsPoisoned)
        {
            return ManagementResults.BadRequest("Handler status is not poisoned.");
        }

        entity.Requeue();

        try
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ManagementResults.Conflict(
                "Handler status was modified by another operation; retry."
            );
        }
    }
}
