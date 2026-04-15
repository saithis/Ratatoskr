using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Ratatoskr.EfCore.Management;

internal record ContextListItem(string Name, bool HasOutbox, bool HasInbox);

internal record ContextListResponse(List<ContextListItem> Contexts);

internal static class GetContextsEndpoint
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/contexts", Handle);
    }

    private static Ok<ContextListResponse> Handle(EfCoreManagementProviderLookup lookup)
    {
        var contexts = lookup.All
            .Select(p => new ContextListItem(p.DbContextName, p.HasOutbox, p.HasInbox))
            .ToList();
        return TypedResults.Ok(new ContextListResponse(contexts));
    }
}
