using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Inbox;

internal static class GetInboxHandlerDetailEndpoint
{
    internal static void Map(IEndpointRouteBuilder inboxGroup)
    {
        inboxGroup.MapGet("/poisoned/{handlerStatusId:guid}", HandleAsync);
    }

    private static async Task<Results<Ok<InboxHandlerDetail>, ProblemHttpResult>> HandleAsync(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementDbContextLookup lookup,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        var logger = loggerFactory.CreateLogger(typeof(GetInboxHandlerDetailEndpoint).FullName!);
        if (
            ManagementDbContextResolver.EnsureInbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        var result = await (
            from hs in db.Set<InboxHandlerStatusEntity>().AsNoTracking()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.Id == handlerStatusId
            select new { hs, msg }
        ).FirstOrDefaultAsync(ct);

        if (result is null)
        {
            return ManagementResults.NotFound($"Handler status '{handlerStatusId}' was not found.");
        }

        if (!result.hs.IsPoisoned)
        {
            return ManagementResults.BadRequest("Handler status is not poisoned.");
        }

        var props = result.msg.GetProperties();
        var (jsonPayload, base64) = ManagementHelpers.DecodeContent(result.msg.Content, logger);

        return TypedResults.Ok(
            new InboxHandlerDetail(
                result.hs.Id,
                result.hs.MessageId,
                props.Type ?? "(unknown)",
                result.hs.HandlerKey,
                result.msg.ReceivedAt,
                result.hs.ErrorCount,
                result.hs.RequeuedCount,
                string.IsNullOrEmpty(result.hs.LastError) ? null : result.hs.LastError,
                props,
                jsonPayload,
                base64,
                contextName
            )
        );
    }

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record InboxHandlerDetail(
        Guid HandlerStatusId,
        string MessageId,
        string MessageType,
        string HandlerKey,
        DateTimeOffset ReceivedAt,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        MessageProperties Properties,
        string? JsonPayload,
        string PayloadBase64,
        string DbContext
    );
}
