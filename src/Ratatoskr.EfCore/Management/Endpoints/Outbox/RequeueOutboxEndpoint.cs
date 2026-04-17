using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static class RequeueOutboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        outboxGroup.MapPost("/poisoned/{id:guid}/requeue", Handle);
    }

    private static async Task<Results<Ok, ProblemHttpResult>> Handle(
        string contextName,
        Guid id,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        if (ManagementProviderResolver.EnsureOutbox(lookup, contextName, out var provider) is { } resolveError)
            return resolveError;

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var outcome = await RequeueHelper.RequeueOutboxAsync(db, id, ct);
        return outcome switch
        {
            SingleRequeueOutcome.Success => TypedResults.Ok(),
            SingleRequeueOutcome.NotFound => ManagementResults.NotFound($"Outbox message '{id}' was not found."),
            SingleRequeueOutcome.NotPoisoned => ManagementResults.BadRequest("Outbox message is not poisoned."),
            _ => ManagementResults.Conflict("Outbox message was modified by another operation; retry."),
        };
    }
}
