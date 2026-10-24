using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class ListPoisonedInboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapGet("/poisoned", HandleAsync);
    }

    private static async Task<
        Results<Ok<PoisonedListResponse<InboxPoisonedListItem>>, ProblemHttpResult>
    > HandleAsync(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        ILoggerFactory loggerFactory,
        int pageSize = PaginationOptions.DefaultPageSize,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? search = null,
        CancellationToken ct = default
    )
    {
        var logger = loggerFactory.CreateLogger(typeof(ListPoisonedInboxEndpoint).FullName!);
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        var poisoned =
            from hs in db.Set<InboxHandlerStatusEntity>().AsNoTracking()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.IsPoisoned
            select new { hs, msg };

        return await PoisonedListQuery
            .For(poisoned, x => x.msg.ReceivedAt, x => x.hs.Id, x => x.msg.SerializedProperties)
            .ExecuteAsync(
                new PoisonedListRequest(contextName, pageSize, cursor, from, to, search),
                x => new
                {
                    x.hs.Id,
                    x.hs.MessageId,
                    x.hs.HandlerKey,
                    x.hs.ErrorCount,
                    x.hs.RequeuedCount,
                    x.hs.LastError,
                    x.msg.ReceivedAt,
                    x.msg.SerializedProperties,
                },
                x => new InboxPoisonedListItem(
                    x.Id,
                    x.MessageId,
                    ManagementHelpers.ExtractType(x.SerializedProperties, logger),
                    x.HandlerKey,
                    x.ReceivedAt,
                    x.ErrorCount,
                    x.RequeuedCount,
                    string.IsNullOrEmpty(x.LastError) ? null : x.LastError,
                    contextName
                ),
                logger,
                ct
            );
    }

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record InboxPoisonedListItem(
        Guid HandlerStatusId,
        string MessageId,
        string MessageType,
        string HandlerKey,
        DateTimeOffset ReceivedAt,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        string DbContext
    );
}
