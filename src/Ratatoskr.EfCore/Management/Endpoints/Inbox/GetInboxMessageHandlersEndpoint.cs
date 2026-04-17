using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class GetInboxMessageHandlersEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapGet("/messages/{messageId}/handlers", Handle);
    }

    private static async Task<Results<Ok<InboxMessageHandlers>, ProblemHttpResult>> Handle(
        string contextName,
        string messageId,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(GetInboxMessageHandlersEndpoint).FullName!);
        if (ManagementProviderResolver.EnsureInbox(lookup, contextName, out var provider) is { } resolveError)
            return resolveError;

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var msg = await db.Set<InboxMessageEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == messageId, ct);
        if (msg is null)
            return ManagementResults.NotFound($"Inbox message '{messageId}' was not found.");

        var handlers = await db.Set<InboxHandlerStatusEntity>()
            .AsNoTracking()
            .Where(x => x.MessageId == messageId)
            .ToListAsync(ct);

        var msgType = ManagementHelpers.ExtractType(msg.SerializedProperties, logger);
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
