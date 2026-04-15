using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class RequeueHelper
{
    // Returns IResult: 200 OK | 404 Not Found | 400 Bad Request | 409 Conflict
    internal static async Task<IResult> RequeueOutboxAsync(
        DbContext dbContext,
        Guid id,
        CancellationToken ct)
    {
        var entity = await dbContext.Set<OutboxMessageEntity>()
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (entity is null) return Results.NotFound();
        if (!entity.IsPoisoned) return Results.BadRequest("Message is not poisoned.");

        entity.Requeue();

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return Results.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict("Message was modified concurrently. Refresh and retry.");
        }
    }

    internal static async Task<IResult> RequeueInboxHandlerAsync(
        DbContext dbContext,
        Guid handlerStatusId,
        CancellationToken ct)
    {
        var entity = await dbContext.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == handlerStatusId, ct);

        if (entity is null) return Results.NotFound();
        if (!entity.IsPoisoned) return Results.BadRequest("Handler status is not poisoned.");

        entity.Requeue();

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return Results.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict("Handler status was modified concurrently. Refresh and retry.");
        }
    }
}
