using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal static class GetOutboxDetailEndpoint
{
    internal static void Map(RouteGroupBuilder outboxGroup)
    {
        outboxGroup.MapGet("/poisoned/{id:guid}", Handle);
    }

    private static async Task<Results<Ok<OutboxPoisonedDetail>, ProblemHttpResult>> Handle(
        string contextName,
        Guid id,
        EfCoreManagementProviderLookup lookup,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(GetOutboxDetailEndpoint).FullName!);
        if (ManagementProviderResolver.EnsureOutbox(lookup, contextName, out var provider) is { } resolveError)
            return resolveError;

        using var scope = scopeFactory.CreateScope();
        var db = provider.GetDbContext(scope.ServiceProvider);

        var entity = await db.Set<OutboxMessageEntity>()
            .AsNoTracking()
            .Where(x => x.Id == id && x.IsPoisoned)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return ManagementResults.NotFound($"Poisoned outbox message '{id}' was not found.");

        var props = ManagementHelpers.SafeDeserializeToJsonElement(entity.SerializedProperties, logger);
        var msgType = ManagementHelpers.ExtractType(entity.SerializedProperties, logger);
        var (jsonPayload, base64) = ManagementHelpers.DecodeContent(entity.Content, logger);

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
