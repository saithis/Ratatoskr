using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.Endpoints;
using Ratatoskr.EfCore.Management.Dto;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Registers Ratatoskr EF Core management API endpoints.
/// Aggregates across all registered <see cref="IEfCoreManagementDbContextProvider"/> implementations
/// so that multiple DbContext registrations do not produce duplicate routes.
/// </summary>
internal sealed class EfCoreEndpointConfigurator(
    IEnumerable<IEfCoreManagementDbContextProvider> providers)
    : IRatatoskrEndpointConfigurator
{
    private readonly List<IEfCoreManagementDbContextProvider> _providers = providers.ToList();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, string policyName)
    {
        var group = endpoints
            .MapGroup("/ratatoskr/api/v1")
            .RequireAuthorization(policyName)
            .DisableAntiforgery();

        // ── Outbox ───────────────────────────────────────────────────────────

        group.MapGet("/outbox/poisoned", async (
            int pageSize = 50,
            string? cursor = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            string? type = null,
            CancellationToken ct = default) =>
        {
            var allItems = new List<OutboxPoisonedListItemDto>();
            long totalCount = 0;

            foreach (var p in _providers.Where(p => p.HasOutbox))
            {
                var (items, count) = await p.GetPoisonedOutboxAsync(pageSize, cursor, from, to, type, ct);
                allItems.AddRange(items);
                totalCount += count;
            }

            allItems = allItems.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(pageSize).ToList();
            var nextCursor = allItems.Count == pageSize
                ? CursorHelper.EncodeCursor(allItems[^1].Id)
                : null;
            return Results.Ok(new PaginatedResponse<OutboxPoisonedListItemDto>(allItems, totalCount, nextCursor));
        });

        group.MapGet("/outbox/poisoned/{id:guid}", async (Guid id, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasOutbox))
            {
                var detail = await p.GetPoisonedOutboxDetailAsync(id, ct);
                if (detail is not null) return Results.Ok(detail);
            }
            return Results.NotFound();
        });

        group.MapPost("/outbox/poisoned/{id:guid}/requeue", async (Guid id, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasOutbox))
            {
                var result = await p.RequeueOutboxAsync(id, ct);
                if (!IsNotFound(result)) return result;
            }
            return Results.NotFound();
        });

        group.MapDelete("/outbox/poisoned/{id:guid}", async (Guid id, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasOutbox))
            {
                var result = await p.DeleteOutboxAsync(id, ct);
                if (!IsNotFound(result)) return result;
            }
            return Results.NotFound();
        });

        group.MapPost("/outbox/poisoned/requeue", async (BulkActionRequest req, CancellationToken ct) =>
        {
            var all = req.All is true;
            var succeeded = new List<Guid>();
            var failed = new List<BulkFailure>();

            foreach (var p in _providers.Where(p => p.HasOutbox))
            {
                var result = await p.BulkRequeueOutboxAsync(req.Ids, all, ct);
                succeeded.AddRange(result.Succeeded);
                failed.AddRange(result.Failed);
            }

            return Results.Ok(new BulkActionResult(succeeded, failed));
        });

        group.MapDelete("/outbox/poisoned", async ([FromBody] BulkActionRequest req, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasOutbox))
                await p.BulkDeleteOutboxAsync(req.Ids, req.All is true, ct);
            return Results.Ok();
        });

        // ── Inbox ─────────────────────────────────────────────────────────────

        group.MapGet("/inbox/poisoned", async (
            int pageSize = 50,
            string? cursor = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            string? type = null,
            CancellationToken ct = default) =>
        {
            var allItems = new List<InboxPoisonedListItemDto>();
            long totalCount = 0;

            foreach (var p in _providers.Where(p => p.HasInbox))
            {
                var (items, count) = await p.GetPoisonedInboxAsync(pageSize, cursor, from, to, type, ct);
                allItems.AddRange(items);
                totalCount += count;
            }

            allItems = allItems.OrderBy(x => x.ReceivedAt).ThenBy(x => x.HandlerStatusId).Take(pageSize).ToList();
            var nextCursor = allItems.Count == pageSize
                ? CursorHelper.EncodeCursor(allItems[^1].HandlerStatusId)
                : null;
            return Results.Ok(new PaginatedResponse<InboxPoisonedListItemDto>(allItems, totalCount, nextCursor));
        });

        group.MapGet("/inbox/poisoned/{handlerStatusId:guid}", async (Guid handlerStatusId, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasInbox))
            {
                var detail = await p.GetPoisonedInboxDetailAsync(handlerStatusId, ct);
                if (detail is not null) return Results.Ok(detail);
            }
            return Results.NotFound();
        });

        group.MapGet("/inbox/messages/{messageId}/handlers", async (string messageId, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasInbox))
            {
                var result = await p.GetInboxHandlersForMessageAsync(messageId, ct);
                if (result is not null) return Results.Ok(result);
            }
            return Results.NotFound();
        });

        group.MapPost("/inbox/poisoned/{handlerStatusId:guid}/requeue", async (Guid handlerStatusId, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasInbox))
            {
                var result = await p.RequeueInboxHandlerAsync(handlerStatusId, ct);
                if (!IsNotFound(result)) return result;
            }
            return Results.NotFound();
        });

        group.MapPost("/inbox/messages/{messageId}/requeue", async (string messageId, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasInbox))
            {
                var result = await p.RequeueAllInboxHandlersForMessageAsync(messageId, ct);
                if (!IsNotFound(result)) return result;
            }
            return Results.NotFound();
        });

        group.MapDelete("/inbox/poisoned/{handlerStatusId:guid}", async (Guid handlerStatusId, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasInbox))
            {
                var result = await p.DeleteInboxHandlerStatusAsync(handlerStatusId, ct);
                if (!IsNotFound(result)) return result;
            }
            return Results.NotFound();
        });

        group.MapPost("/inbox/poisoned/requeue", async (BulkActionRequest req, CancellationToken ct) =>
        {
            var all = req.All is true;
            var succeeded = new List<Guid>();
            var failed = new List<BulkFailure>();

            foreach (var p in _providers.Where(p => p.HasInbox))
            {
                var result = await p.BulkRequeueInboxAsync(req.Ids, all, ct);
                succeeded.AddRange(result.Succeeded);
                failed.AddRange(result.Failed);
            }

            return Results.Ok(new BulkActionResult(succeeded, failed));
        });

        group.MapDelete("/inbox/poisoned", async ([FromBody] BulkActionRequest req, CancellationToken ct) =>
        {
            foreach (var p in _providers.Where(p => p.HasInbox))
                await p.BulkDeleteInboxAsync(req.Ids, req.All is true, ct);
            return Results.Ok();
        });

        // ── Health ─────────────────────────────────────────────────────────────

        group.MapGet("/health", async (CancellationToken ct) =>
        {
            var dtos = new List<DbContextHealthDto>();
            foreach (var p in _providers)
                dtos.Add(await p.GetHealthAsync(ct));
            return Results.Ok(new HealthOverviewDto(dtos));
        });
    }

    private static bool IsNotFound(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: 404 };
}
