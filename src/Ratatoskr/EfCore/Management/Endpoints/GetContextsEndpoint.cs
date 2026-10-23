using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management.Endpoints;

internal static class GetContextsEndpoint
{
    internal static void Map(IEndpointRouteBuilder group)
    {
        group.MapGet("/contexts", Handle);
    }

    private static Ok<ContextListResponse> Handle(EfCoreManagementDbContextLookup lookup)
    {
        // Order is deterministic so the UI doesn't reshuffle tabs between refreshes just
        // because DI happened to resolve providers in a different order.
        var contexts = lookup
            .All.OrderBy(p => p.DbContextName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ContextListItem(p.DbContextName, p.HasOutbox, p.HasInbox))
            .ToList();
        return TypedResults.Ok(new ContextListResponse(contexts));
    }

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record ContextListItem(string Name, bool HasOutbox, bool HasInbox);

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record ContextListResponse(List<ContextListItem> Contexts);
}
