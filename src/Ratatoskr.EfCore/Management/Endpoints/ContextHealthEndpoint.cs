using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management.Endpoints;

internal static class ContextHealthEndpoint
{
    internal static void Map(IEndpointRouteBuilder contextGroup)
    {
        contextGroup.MapGet("/health", Handle);
    }

    private static Results<Ok<ContextHealthResponse>, ProblemHttpResult> Handle(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        EfCoreMetricsState metricsState
    )
    {
        if (
            ManagementDbContextResolver.EnsureContext(lookup, contextName, out var dbContext) is
            { } resolveError
        )
            return resolveError;

        var descriptor = lookup.Find(contextName)!;
        metricsState.TryGetValue(dbContext.GetType(), out var metrics);

        return TypedResults.Ok(
            new ContextHealthResponse(
                descriptor.DbContextName,
                metrics.PoisonedOutboxCount,
                metrics.PoisonedInboxCount,
                metrics.PendingOutboxCount,
                metrics.PendingInboxCount,
                descriptor.LastOutboxProcessingAt,
                descriptor.LastInboxProcessingAt
            )
        );
    }

    internal record ContextHealthResponse(
        string DbContextName,
        long PoisonedOutboxCount,
        long PoisonedInboxCount,
        long PendingOutboxCount,
        long PendingInboxCount,
        DateTimeOffset? LastOutboxProcessedAt,
        DateTimeOffset? LastInboxProcessedAt
    );
}
