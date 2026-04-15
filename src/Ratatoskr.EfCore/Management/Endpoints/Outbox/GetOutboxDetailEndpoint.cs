using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record OutboxPoisonedDetail(
    Guid Id,
    string MessageType,
    DateTimeOffset CreatedAt,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    DateTimeOffset? FailedAt,
    JsonElement Properties,
    string? JsonPayload,
    string PayloadBase64,
    string DbContext);

internal static class GetOutboxDetailEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapGet("/poisoned/{id:guid}", Handle);
    }

    private static async Task<Results<Ok<OutboxPoisonedDetail>, NotFound>> Handle(
        string contextName,
        Guid id,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        var detail = await provider.GetPoisonedOutboxDetailAsync(id, ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }
}
