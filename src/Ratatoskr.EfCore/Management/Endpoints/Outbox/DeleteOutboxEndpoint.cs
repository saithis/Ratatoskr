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

    private static async Task<Results<Ok, ProblemHttpResult>> Handle(
        string contextName,
        Guid id,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        if (ManagementProviderResolver.EnsureOutbox(lookup, contextName, out var provider) is { } resolveError)
            return resolveError;

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var entity = await db.Set<OutboxMessageEntity>()
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (entity is null) return ManagementResults.NotFound($"Outbox message '{id}' was not found.");
        if (!entity.IsPoisoned) return ManagementResults.BadRequest("Outbox message is not poisoned.");

        db.Set<OutboxMessageEntity>().Remove(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ManagementResults.Conflict("Outbox message was modified by another operation; retry.");
        }
    }
}
