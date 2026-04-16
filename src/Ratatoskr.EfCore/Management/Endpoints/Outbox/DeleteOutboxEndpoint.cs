using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class DeleteOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapDelete("/poisoned/{id:guid}", Handle);
    }

    private static async Task<Results<Ok, NotFound, BadRequest<string>, Conflict>> Handle(
        string contextName,
        Guid id,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var entity = await db.Set<OutboxMessageEntity>()
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (entity is null) return TypedResults.NotFound();
        if (!entity.IsPoisoned) return TypedResults.BadRequest("Message is not poisoned.");

        db.Set<OutboxMessageEntity>().Remove(entity);
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
