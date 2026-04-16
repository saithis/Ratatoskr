using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class DeleteInboxHandlerEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapDelete("/poisoned/{handlerStatusId:guid}", Handle);
    }

    private static async Task<Results<Ok, ProblemHttpResult>> Handle(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        if (ManagementProviderResolver.EnsureInbox(lookup, contextName, out var provider) is { } resolveError)
            return resolveError;

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var entity = await db.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == handlerStatusId, ct);

        if (entity is null)
            return ManagementResults.NotFound($"Inbox handler status '{handlerStatusId}' was not found.");
        if (!entity.IsPoisoned)
            return ManagementResults.BadRequest("Handler status is not poisoned.");

        var messageId = entity.MessageId;
        db.Set<InboxHandlerStatusEntity>().Remove(entity);

        // Handler removal + orphan check + parent removal must all succeed or all fail together.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return ManagementResults.Conflict("Handler status was modified by another operation; retry.");
        }

        // Orphan-cleanup is expressed as a single SQL statement with a NOT EXISTS guard so the
        // parent is only deleted when no other handler rows reference it at delete time —
        // tighter than a separate COUNT + remove which had a TOCTOU hole for concurrent inserts.
        await db.Set<InboxMessageEntity>()
            .Where(m => m.Id == messageId
                        && !db.Set<InboxHandlerStatusEntity>().Any(h => h.MessageId == m.Id))
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return TypedResults.Ok();
    }
}
