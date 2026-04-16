using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class BulkDeleteOutboxEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapDelete("/poisoned", Handle);
    }

    private static async Task<Results<Ok, ProblemHttpResult>> Handle(
        string contextName,
        [FromBody] BulkDeleteOutboxRequest req,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox)
            return ManagementResults.NotFound($"No outbox is registered for DbContext '{contextName}'.");

        if (!BulkRequestValidator.TryValidate(req.Ids, req.All, out var error))
            return ManagementResults.BadRequest(error!);

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        if (req.All is true)
        {
            await db.Set<OutboxMessageEntity>()
                .Where(x => x.IsPoisoned)
                .ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.Set<OutboxMessageEntity>()
                .Where(x => req.Ids!.Contains(x.Id) && x.IsPoisoned)
                .ExecuteDeleteAsync(ct);
        }

        return TypedResults.Ok();
    }

    internal record BulkDeleteOutboxRequest(List<Guid>? Ids, bool? All);
}
