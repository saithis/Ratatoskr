using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class BulkDeleteInboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapDelete("/poisoned", HandleByIds);
        inboxGroup.MapDelete("/poisoned/all", HandleAll);
    }

    private static async Task<Results<Ok, ProblemHttpResult>> HandleByIds(
        string contextName,
        [FromBody] BulkDeleteInboxRequest req,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
            return resolveError;

        if (!BulkRequestValidator.TryValidateIds(req.Ids, out var error))
            return ManagementResults.BadRequest(error!);

        // Whole operation must be atomic: if orphaned-parent cleanup fails after the
        // handler rows are deleted, rolling back keeps the poisoned handler rows in
        // place so the operator can retry.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var messageIds = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => req.Ids!.Contains(x.Id) && x.IsPoisoned)
            .Select(x => x.MessageId)
            .Distinct()
            .ToListAsync(ct);

        await db.Set<InboxHandlerStatusEntity>()
            .Where(x => req.Ids!.Contains(x.Id) && x.IsPoisoned)
            .ExecuteDeleteAsync(ct);

        await DeleteOrphanedMessagesAsync(db, messageIds, ct);

        await tx.CommitAsync(ct);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, ProblemHttpResult>> HandleAll(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        CancellationToken ct
    )
    {
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
            return resolveError;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var affectedMessageIds = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.IsPoisoned)
            .Select(x => x.MessageId)
            .Distinct()
            .ToListAsync(ct);

        await db.Set<InboxHandlerStatusEntity>().Where(x => x.IsPoisoned).ExecuteDeleteAsync(ct);

        await DeleteOrphanedMessagesAsync(db, affectedMessageIds, ct);

        await tx.CommitAsync(ct);
        return TypedResults.Ok();
    }

    /// <summary>
    /// Deletes <see cref="InboxMessageEntity"/> rows whose message IDs are in
    /// <paramref name="messageIds"/> and that no longer have any handler status rows.
    /// </summary>
    private static async Task DeleteOrphanedMessagesAsync(
        DbContext db,
        List<string> messageIds,
        CancellationToken ct
    )
    {
        if (messageIds.Count == 0)
            return;

        await db.Set<InboxMessageEntity>()
            .Where(msg =>
                messageIds.Contains(msg.Id)
                && !db.Set<InboxHandlerStatusEntity>().Any(hs => hs.MessageId == msg.Id)
            )
            .ExecuteDeleteAsync(ct);
    }

    internal record BulkDeleteInboxRequest(List<Guid>? Ids);
}
