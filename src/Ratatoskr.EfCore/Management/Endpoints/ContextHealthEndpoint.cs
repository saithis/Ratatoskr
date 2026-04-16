using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal static class ContextHealthEndpoint
{
    internal static void Map(RouteGroupBuilder contextGroup)
    {
        contextGroup.MapGet("/health", Handle);
    }

    private static Results<Ok<ContextHealthResponse>, NotFound> Handle(
        string contextName,
        EfCoreManagementProviderLookup lookup)
    {
        var provider = lookup.Find(contextName);
        if (provider is null) return TypedResults.NotFound();

        provider.MetricsState.ContextMetrics.TryGetValue(provider.MetricsContextKey, out var metrics);

        return TypedResults.Ok(new ContextHealthResponse(
            provider.DbContextName,
            metrics.PoisonedOutboxCount,
            metrics.PoisonedInboxCount,
            metrics.PendingOutboxCount,
            metrics.PendingInboxCount,
            provider.LastOutboxProcessingAt,
            provider.LastInboxProcessingAt));
    }

    internal record ContextHealthResponse(
        string DbContextName,
        long PoisonedOutboxCount,
        long PoisonedInboxCount,
        long PendingOutboxCount,
        long PendingInboxCount,
        DateTimeOffset? LastOutboxProcessedAt,
        DateTimeOffset? LastInboxProcessedAt);
}
