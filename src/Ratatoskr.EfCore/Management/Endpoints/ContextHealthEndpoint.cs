using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record ContextHealthResponse(
    string DbContextName,
    long PoisonedOutboxCount,
    long PoisonedInboxCount,
    long PendingOutboxCount,
    long PendingInboxCount,
    DateTimeOffset? LastOutboxProcessedAt,
    DateTimeOffset? LastInboxProcessedAt);

internal static class ContextHealthEndpoint
{
    internal static void Map(RouteGroupBuilder contextGroup)
    {
        contextGroup.MapGet("/health", Handle);
    }

    private static async Task<Results<Ok<ContextHealthResponse>, NotFound>> Handle(
        string contextName,
        EfCoreManagementProviderLookup lookup,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null) return TypedResults.NotFound();

        var health = await provider.GetHealthAsync(ct);
        return TypedResults.Ok(health);
    }
}
