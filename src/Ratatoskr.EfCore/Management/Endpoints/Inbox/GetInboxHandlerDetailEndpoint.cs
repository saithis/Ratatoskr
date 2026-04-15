using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

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

internal static class GetInboxHandlerDetailEndpoint
{
    internal static void Map(RouteGroupBuilder inboxGroup)
    {
        inboxGroup.MapGet("/poisoned/{handlerStatusId:guid}", Handle);
    }

    private static async Task<Results<Ok<InboxHandlerDetail>, NotFound>> Handle(
        string contextName,
        Guid handlerStatusId,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasInbox) return TypedResults.NotFound();

        var detail = await provider.GetPoisonedInboxDetailAsync(handlerStatusId, ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }
}
