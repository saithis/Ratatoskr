using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class GetInboxHandlerDetailEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapGet("/poisoned/{handlerStatusId:guid}", Handle);
    }

    private static async Task<Results<Ok<InboxHandlerDetail>, ProblemHttpResult>> Handle(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(GetInboxHandlerDetailEndpoint).FullName!);
        if (ManagementProviderResolver.EnsureInbox(lookup, contextName, out var provider) is { } resolveError)
            return resolveError;

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var result = await (
            from hs in db.Set<InboxHandlerStatusEntity>()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.Id == handlerStatusId && hs.IsPoisoned
            select new { hs, msg }
        ).FirstOrDefaultAsync(ct);

        if (result is null)
            return ManagementResults.NotFound($"Poisoned handler status '{handlerStatusId}' was not found.");

        var props = ManagementHelpers.SafeDeserializeToJsonElement(result.msg.SerializedProperties, logger);
        var msgType = ManagementHelpers.ExtractType(result.msg.SerializedProperties, logger);
        var (jsonPayload, base64) = ManagementHelpers.DecodeContent(result.msg.Content, logger);

        return TypedResults.Ok(new InboxHandlerDetail(
            result.hs.Id, result.hs.MessageId, msgType, result.hs.HandlerKey, result.msg.ReceivedAt,
            result.hs.ErrorCount, result.hs.RequeuedCount,
            string.IsNullOrEmpty(result.hs.LastError) ? null : result.hs.LastError,
            props, jsonPayload, base64, provider.DbContextName));
    }

    internal record InboxHandlerDetail(
        Guid HandlerStatusId,
        string MessageId,
        string MessageType,
        string HandlerKey,
        DateTimeOffset ReceivedAt,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        JsonElement Properties,
        string? JsonPayload,
        string PayloadBase64,
        string DbContext);
}
