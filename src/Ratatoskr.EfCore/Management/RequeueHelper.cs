using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class RequeueHelper
{
    internal static async Task<SingleRequeueOutcome> RequeueOutboxAsync(
        DbContext dbContext,
        Guid id,
        CancellationToken ct)
    {
        var entity = await dbContext.Set<OutboxMessageEntity>()
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (entity is null) return SingleRequeueOutcome.NotFound;
        if (!entity.IsPoisoned) return SingleRequeueOutcome.NotPoisoned;

        entity.Requeue();

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return SingleRequeueOutcome.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return SingleRequeueOutcome.Conflict;
        }
    }

    internal static async Task<SingleRequeueOutcome> RequeueInboxHandlerAsync(
        DbContext dbContext,
        Guid handlerStatusId,
        CancellationToken ct)
    {
        var entity = await dbContext.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == handlerStatusId, ct);

        if (entity is null) return SingleRequeueOutcome.NotFound;
        if (!entity.IsPoisoned) return SingleRequeueOutcome.NotPoisoned;

        entity.Requeue();

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return SingleRequeueOutcome.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return SingleRequeueOutcome.Conflict;
        }
    }
}
