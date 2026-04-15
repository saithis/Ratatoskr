using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

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
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        var result = await provider.GetInboxHandlersForMessageAsync(messageId, ct);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }
}
