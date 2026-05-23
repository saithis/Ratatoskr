using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static class GetOutboxDetailEndpoint
{
    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        outboxGroup.MapGet("/poisoned/{id:guid}", Handle);
    }

    private static async Task<Results<Ok<OutboxPoisonedDetail>, ProblemHttpResult>> Handle(
        string contextName,
        Guid id,
        EfCoreManagementDbContextLookup lookup,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        var logger = loggerFactory.CreateLogger(typeof(GetOutboxDetailEndpoint).FullName!);
        if (
            ManagementDbContextResolver.EnsureOutbox(lookup, contextName, out var db) is
            { } resolveError
        )
            return resolveError;

        var entity = await db.Set<OutboxMessageEntity>()
            .AsNoTracking()
            .Where(x => x.Id == id && x.IsPoisoned)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return ManagementResults.NotFound($"Poisoned outbox message '{id}' was not found.");

        var props = entity.GetProperties();
        var (jsonPayload, base64) = ManagementHelpers.DecodeContent(entity.Content, logger);

        return TypedResults.Ok(
            new OutboxPoisonedDetail(
                entity.Id,
                props.Type ?? "(unknown)",
                entity.CreatedAt,
                entity.ErrorCount,
                entity.RequeuedCount,
                string.IsNullOrEmpty(entity.Error) ? null : entity.Error,
                entity.FailedAt,
                props,
                jsonPayload,
                base64,
                contextName
            )
        );
    }

    internal record OutboxPoisonedDetail(
        Guid Id,
        string MessageType,
        DateTimeOffset CreatedAt,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        DateTimeOffset? FailedAt,
        MessageProperties Properties,
        string? JsonPayload,
        string PayloadBase64,
        string DbContext
    );
}
