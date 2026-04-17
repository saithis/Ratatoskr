using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class RequeueInboxMessageEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapPost("/messages/{messageId}/requeue", Handle);
    }

    private static async Task<Results<Ok<RequeueInboxMessageResponse>, ProblemHttpResult>> Handle(
        string contextName,
        string messageId,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct)
    {
        if (ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is { } resolveError)
            return resolveError;

        var handlers = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.MessageId == messageId && x.IsPoisoned)
            .ToListAsync(ct);

        if (handlers.Count == 0)
            return ManagementResults.NotFound($"No poisoned handlers found for inbox message '{messageId}'.");

        foreach (var h in handlers)
            h.Requeue();

        try
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok(new RequeueInboxMessageResponse(handlers.Select(h => h.Id).ToList()));
        }
        catch (DbUpdateConcurrencyException)
        {
            return ManagementResults.Conflict("One or more handlers were modified concurrently; retry.");
        }
    }

    internal record RequeueInboxMessageResponse(List<Guid> RequeuedHandlerStatusIds);
}
