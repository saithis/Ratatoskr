using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class BulkDeleteInboxEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapDelete("/poisoned", Handle);
    }

    private static async Task<Results<Ok, NotFound, BadRequest<string>>> Handle(
        string contextName,
        [FromBody] BulkDeleteInboxRequest req,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        if (!BulkRequestValidator.TryValidate(req.Ids, req.All, out var error))
            return TypedResults.BadRequest(error!);

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        // Whole operation must be atomic: if orphaned-parent cleanup fails after the
        // handler rows are deleted, rolling back keeps the poisoned handler rows in
        // place so the operator can retry.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (req.All is true)
        {
            var affectedMessageIds = await db.Set<InboxHandlerStatusEntity>()
                .Where(x => x.IsPoisoned)
                .Select(x => x.MessageId)
                .Distinct()
                .ToListAsync(ct);

            await db.Set<InboxHandlerStatusEntity>()
                .Where(x => x.IsPoisoned)
                .ExecuteDeleteAsync(ct);

            await DeleteOrphanedMessagesAsync(db, affectedMessageIds, ct);
        }
        else
        {
            var messageIds = await db.Set<InboxHandlerStatusEntity>()
                .Where(x => req.Ids!.Contains(x.Id) && x.IsPoisoned)
                .Select(x => x.MessageId)
                .Distinct()
                .ToListAsync(ct);

            await db.Set<InboxHandlerStatusEntity>()
                .Where(x => req.Ids!.Contains(x.Id) && x.IsPoisoned)
                .ExecuteDeleteAsync(ct);

            await DeleteOrphanedMessagesAsync(db, messageIds, ct);
        }

        await tx.CommitAsync(ct);
        return TypedResults.Ok();
    }

    /// <summary>
    /// Deletes <see cref="InboxMessageEntity"/> rows whose message IDs are in
    /// <paramref name="messageIds"/> and that no longer have any handler status rows.
    /// </summary>
    private static async Task DeleteOrphanedMessagesAsync(
        Microsoft.EntityFrameworkCore.DbContext db, List<string> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0) return;

        // Delete in one shot: parent messages whose ID is in the affected set and
        // that have no remaining handler status rows.
        await db.Set<InboxMessageEntity>()
            .Where(msg => messageIds.Contains(msg.Id) &&
                          !db.Set<InboxHandlerStatusEntity>().Any(hs => hs.MessageId == msg.Id))
            .ExecuteDeleteAsync(ct);
    }

    internal record BulkDeleteInboxRequest(List<Guid>? Ids, bool? All);
}
