using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management.Endpoints.Outbox;

internal static class ListPoisonedOutboxEndpoint
{
    internal static void Map(IEndpointRouteBuilder outboxGroup)
    {
        outboxGroup.MapGet("/poisoned", HandleAsync);
    }

    private static async Task<
        Results<Ok<PoisonedListResponse<OutboxPoisonedListItem>>, ProblemHttpResult>
    > HandleAsync(
        string contextName,
        EfCoreManagementDbContextLookup lookup,
        ILoggerFactory loggerFactory,
        int pageSize = PaginationOptions.DefaultPageSize,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? search = null,
        CancellationToken ct = default
    )
    {
        var logger = loggerFactory.CreateLogger(typeof(ListPoisonedOutboxEndpoint).FullName!);
        if (
            ManagementDbContextResolver.EnsureOutbox(lookup, contextName, out var db) is
            { } resolveError
        )
        {
            return resolveError;
        }

        return await PoisonedListQuery
            .For(
                db.Set<OutboxMessageEntity>().AsNoTracking().Where(x => x.IsPoisoned),
                x => x.CreatedAt,
                x => x.Id,
                x => x.SerializedProperties
            )
            .ExecuteAsync(
                new PoisonedListRequest(contextName, pageSize, cursor, from, to, search),
                x => new
                {
                    x.Id,
                    x.SerializedProperties,
                    x.CreatedAt,
                    x.ErrorCount,
                    x.RequeuedCount,
                    x.Error,
                },
                x => new OutboxPoisonedListItem(
                    x.Id,
                    ManagementHelpers.ExtractType(x.SerializedProperties, logger),
                    x.CreatedAt,
                    x.ErrorCount,
                    x.RequeuedCount,
                    string.IsNullOrEmpty(x.Error) ? null : x.Error,
                    contextName
                ),
                logger,
                ct
            );
    }

    [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
    internal record OutboxPoisonedListItem(
        Guid Id,
        string MessageType,
        DateTimeOffset CreatedAt,
        int ErrorCount,
        int RequeuedCount,
        string? LastError,
        string DbContext
    );
}
