using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class GetInboxMessageHandlersEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapGet("/messages/{messageId}/handlers", Handle);
    }

    private static async Task<Results<Ok<InboxMessageHandlers>, NotFound>> Handle(
        string contextName,
        string messageId,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var msg = await db.Set<InboxMessageEntity>()
            .FirstOrDefaultAsync(x => x.Id == messageId, ct);
        if (msg is null) return TypedResults.NotFound();

        var handlers = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.MessageId == messageId)
            .ToListAsync(ct);

        var msgType = ManagementHelpers.ExtractType(msg.SerializedProperties);
        var summaries = handlers.Select(h => new InboxHandlerStatusSummary(
            h.Id, h.HandlerKey, h.ErrorCount, h.RequeuedCount,
            string.IsNullOrEmpty(h.LastError) ? null : h.LastError,
            h.IsPoisoned, h.CompletedAt.HasValue, provider.DbContextName)).ToList();

        return TypedResults.Ok(new InboxMessageHandlers(messageId, msgType, msg.ReceivedAt, summaries));
    }

    internal record InboxHandlerStatusSummary(
        Guid HandlerStatusId,
        string HandlerKey,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        bool IsPoisoned,
        bool IsCompleted,
        string DbContext);

    internal record InboxMessageHandlers(
        string MessageId,
        string MessageType,
        DateTimeOffset ReceivedAt,
        List<InboxHandlerStatusSummary> Handlers);
}
