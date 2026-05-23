using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class GetInboxMessageHandlersEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapGet("/messages/{messageId}/handlers", Handle);
    }

    private static async Task<Results<Ok<InboxMessageHandlers>, ProblemHttpResult>> Handle(
        string contextName,
        string messageId,
        EfCoreManagementDbContextLookup lookup,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        var logger = loggerFactory.CreateLogger(typeof(GetInboxMessageHandlersEndpoint).FullName!);
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
            return resolveError;

        var msg = await db.Set<InboxMessageEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == messageId, ct);
        if (msg is null)
            return ManagementResults.NotFound($"Inbox message '{messageId}' was not found.");

        var handlers = await db.Set<InboxHandlerStatusEntity>()
            .AsNoTracking()
            .Where(x => x.MessageId == messageId)
            .ToListAsync(ct);

        var msgType = msg.GetProperties().Type ?? "(unknown)";
        var summaries = handlers
            .Select(h => new InboxHandlerStatusSummary(
                h.Id,
                h.HandlerKey,
                h.ErrorCount,
                h.RequeuedCount,
                string.IsNullOrEmpty(h.LastError) ? null : h.LastError,
                h.IsPoisoned,
                h.CompletedAt.HasValue,
                contextName
            ))
            .ToList();

        return TypedResults.Ok(
            new InboxMessageHandlers(messageId, msgType, msg.ReceivedAt, summaries)
        );
    }

    internal record InboxHandlerStatusSummary(
        Guid HandlerStatusId,
        string HandlerKey,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        bool IsPoisoned,
        bool IsCompleted,
        string DbContext
    );

    internal record InboxMessageHandlers(
        string MessageId,
        string MessageType,
        DateTimeOffset ReceivedAt,
        List<InboxHandlerStatusSummary> Handlers
    );
}
