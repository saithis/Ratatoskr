using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static class RequeueOutboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        outboxGroup.MapPost("/poisoned/{id:guid}/requeue", Handle);
    }

    private static async Task<Results<Ok, ProblemHttpResult>> Handle(
        string contextName,
        Guid id,
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

        var entity = await db.Set<OutboxMessageEntity>().SingleOrDefaultAsync(x => x.Id == id, ct);

        if (entity is null)
        {
            return ManagementResults.NotFound($"Outbox message '{id}' was not found.");
        }

        if (!entity.IsPoisoned)
        {
            return ManagementResults.BadRequest("Outbox message is not poisoned.");
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
                "Outbox message was modified by another operation; retry."
            );
        }
    }
}
