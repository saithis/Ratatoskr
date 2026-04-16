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

    private static async Task<Results<Ok, NotFound, BadRequest<string>, Conflict>> Handle(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var entity = await db.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == handlerStatusId, ct);

        if (entity is null) return TypedResults.NotFound();
        if (!entity.IsPoisoned) return TypedResults.BadRequest("Handler status is not poisoned.");

        var messageId = entity.MessageId;
        db.Set<InboxHandlerStatusEntity>().Remove(entity);

        // Atomically check whether this is the only remaining handler and delete the orphaned
        // parent message in the same SaveChanges call to avoid a TOCTOU window.
        var otherHandlerCount = await db.Set<InboxHandlerStatusEntity>()
            .CountAsync(x => x.MessageId == messageId && x.Id != handlerStatusId, ct);

        if (otherHandlerCount == 0)
        {
            var parent = await db.Set<InboxMessageEntity>()
                .FindAsync([messageId], ct);
            if (parent is not null)
                db.Set<InboxMessageEntity>().Remove(parent);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Conflict();
        }
    }
}
