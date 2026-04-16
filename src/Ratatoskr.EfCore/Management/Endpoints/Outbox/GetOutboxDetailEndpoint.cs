using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

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
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var provider = lookup.Find(contextName);
        if (provider is null || !provider.HasOutbox) return TypedResults.NotFound();

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var entity = await db.Set<OutboxMessageEntity>()
            .Where(x => x.Id == id && x.IsPoisoned)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return TypedResults.NotFound();

        var props = ManagementHelpers.SafeDeserializeToJsonElement(entity.SerializedProperties);
        var msgType = ManagementHelpers.ExtractType(entity.SerializedProperties);
        var (jsonPayload, base64) = ManagementHelpers.DecodeContent(entity.Content);

        return TypedResults.Ok(new OutboxPoisonedDetail(
            entity.Id, msgType, entity.CreatedAt, entity.ErrorCount, entity.RequeuedCount,
            string.IsNullOrEmpty(entity.Error) ? null : entity.Error,
            entity.FailedAt, props, jsonPayload, base64, provider.DbContextName));
    }

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
}
