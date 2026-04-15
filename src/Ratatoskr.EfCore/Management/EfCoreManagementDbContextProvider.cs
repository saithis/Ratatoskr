using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.EfCore.Management.Dto;

namespace Ratatoskr.EfCore.Management;

internal sealed class EfCoreManagementDbContextProvider<TDbContext> : IEfCoreManagementDbContextProvider
    where TDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EfCoreMetricsState _metricsState;
    private readonly OutboxProcessor<TDbContext>? _outboxProcessor;
    private readonly InboxProcessor<TDbContext>? _inboxProcessor;
    private readonly string _contextKey = typeof(TDbContext).FullName ?? typeof(TDbContext).Name;

    public EfCoreManagementDbContextProvider(
        IServiceScopeFactory scopeFactory,
        EfCoreMetricsState metricsState,
        IServiceProvider serviceProvider)
    {
        _scopeFactory = scopeFactory;
        _metricsState = metricsState;
        _outboxProcessor = serviceProvider.GetService<OutboxProcessor<TDbContext>>();
        _inboxProcessor = serviceProvider.GetService<InboxProcessor<TDbContext>>();
        HasOutbox = serviceProvider.GetService<OutboxOptionsHolder<TDbContext>>() is not null;
        HasInbox = serviceProvider.GetService<InboxOptionsHolder<TDbContext>>() is not null;
    }

    public string DbContextName { get; } = typeof(TDbContext).Name;
    public bool HasOutbox { get; }
    public bool HasInbox { get; }

    // ─── Outbox ───────────────────────────────────────────────────────────────

    public async Task<(List<OutboxPoisonedListItemDto> Items, long TotalCount)> GetPoisonedOutboxAsync(
        int pageSize, string? cursor, DateTimeOffset? from, DateTimeOffset? to, string? type, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var query = db.Set<OutboxMessageEntity>().Where(x => x.IsPoisoned);
        if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.CreatedAt <= to.Value);

        // Cursor: base64url-encoded last-seen Id
        if (cursor is not null)
        {
            var lastId = CursorHelper.DecodeCursor(cursor);
            if (lastId.HasValue)
                query = query.Where(x => x.Id.CompareTo(lastId.Value) > 0);
        }

        var totalCount = await query.LongCountAsync(ct);

        var items = await query
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Take(pageSize)
            .Select(x => new { x.Id, x.SerializedProperties, x.CreatedAt, x.ErrorCount, x.RequeuedCount, x.Error })
            .ToListAsync(ct);

        var dtos = items
            .Select(x =>
            {
                var msgType = ExtractType(x.SerializedProperties);
                if (type is not null && !msgType.Contains(type, StringComparison.OrdinalIgnoreCase))
                    return null;
                return new OutboxPoisonedListItemDto(
                    x.Id, msgType, x.CreatedAt, x.ErrorCount, x.RequeuedCount,
                    string.IsNullOrEmpty(x.Error) ? null : x.Error, DbContextName);
            })
            .Where(x => x is not null)
            .Cast<OutboxPoisonedListItemDto>()
            .ToList();

        return (dtos, totalCount);
    }

    public async Task<OutboxPoisonedDetailDto?> GetPoisonedOutboxDetailAsync(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var entity = await db.Set<OutboxMessageEntity>()
            .Where(x => x.Id == id && x.IsPoisoned)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return null;

        var props = SafeDeserializeToJsonElement(entity.SerializedProperties);
        var msgType = ExtractType(entity.SerializedProperties);
        var (jsonPayload, base64) = DecodeContent(entity.Content);

        return new OutboxPoisonedDetailDto(
            entity.Id, msgType, entity.CreatedAt, entity.ErrorCount, entity.RequeuedCount,
            string.IsNullOrEmpty(entity.Error) ? null : entity.Error,
            entity.FailedAt, props, jsonPayload, base64, DbContextName);
    }

    public async Task<IResult> RequeueOutboxAsync(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await RequeueHelper.RequeueOutboxAsync(db, id, ct);
    }

    public async Task<IResult> DeleteOutboxAsync(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var entity = await db.Set<OutboxMessageEntity>()
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (entity is null) return Results.NotFound();
        if (!entity.IsPoisoned) return Results.BadRequest("Message is not poisoned.");

        db.Set<OutboxMessageEntity>().Remove(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict("Message was modified concurrently. Refresh and retry.");
        }
    }

    public async Task<BulkActionResult> BulkRequeueOutboxAsync(List<Guid>? ids, bool all, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        if (all)
        {
            await db.Set<OutboxMessageEntity>()
                .Where(x => x.IsPoisoned)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsPoisoned, false)
                    .SetProperty(x => x.ErrorCount, 0)
                    .SetProperty(x => x.Error, string.Empty)
                    .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.ProcessingStartedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.RequeuedCount, x => x.RequeuedCount + 1)
                    .SetProperty(x => x.Version, x => x.Version + 1), ct);
            return new BulkActionResult([], []);
        }

        if (ids is null or { Count: 0 })
            return new BulkActionResult([], []);

        var succeeded = new List<Guid>();
        var failed = new List<BulkFailure>();
        foreach (var id in ids)
        {
            var result = await RequeueHelper.RequeueOutboxAsync(db, id, ct);
            if (result is { } r && IsSuccessResult(r))
                succeeded.Add(id);
            else
                failed.Add(new BulkFailure(id, "Not found, not poisoned, or concurrent modification."));
        }
        return new BulkActionResult(succeeded, failed);
    }

    public async Task<IResult> BulkDeleteOutboxAsync(List<Guid>? ids, bool all, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        if (all)
        {
            await db.Set<OutboxMessageEntity>()
                .Where(x => x.IsPoisoned)
                .ExecuteDeleteAsync(ct);
            return Results.Ok();
        }

        if (ids is null or { Count: 0 }) return Results.Ok();

        await db.Set<OutboxMessageEntity>()
            .Where(x => ids.Contains(x.Id) && x.IsPoisoned)
            .ExecuteDeleteAsync(ct);
        return Results.Ok();
    }

    // ─── Inbox ────────────────────────────────────────────────────────────────

    public async Task<(List<InboxPoisonedListItemDto> Items, long TotalCount)> GetPoisonedInboxAsync(
        int pageSize, string? cursor, DateTimeOffset? from, DateTimeOffset? to, string? type, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var query =
            from hs in db.Set<InboxHandlerStatusEntity>()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.IsPoisoned
            select new { hs, msg };

        if (from.HasValue) query = query.Where(x => x.msg.ReceivedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.msg.ReceivedAt <= to.Value);

        if (cursor is not null)
        {
            var lastId = CursorHelper.DecodeCursor(cursor);
            if (lastId.HasValue)
                query = query.Where(x => x.hs.Id.CompareTo(lastId.Value) > 0);
        }

        var totalCount = await query.LongCountAsync(ct);

        var items = await query
            .OrderBy(x => x.msg.ReceivedAt).ThenBy(x => x.hs.Id)
            .Take(pageSize)
            .Select(x => new
            {
                x.hs.Id, x.hs.MessageId, x.hs.HandlerKey,
                x.hs.ErrorCount, x.hs.RequeuedCount, x.hs.LastError,
                x.msg.ReceivedAt, x.msg.SerializedProperties
            })
            .ToListAsync(ct);

        var dtos = items
            .Select(x =>
            {
                var msgType = ExtractType(x.SerializedProperties);
                if (type is not null && !msgType.Contains(type, StringComparison.OrdinalIgnoreCase))
                    return null;
                return new InboxPoisonedListItemDto(
                    x.Id, x.MessageId, msgType, x.HandlerKey, x.ReceivedAt,
                    x.ErrorCount, x.RequeuedCount,
                    string.IsNullOrEmpty(x.LastError) ? null : x.LastError, DbContextName);
            })
            .Where(x => x is not null)
            .Cast<InboxPoisonedListItemDto>()
            .ToList();

        return (dtos, totalCount);
    }

    public async Task<InboxPoisonedDetailDto?> GetPoisonedInboxDetailAsync(Guid handlerStatusId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var result = await (
            from hs in db.Set<InboxHandlerStatusEntity>()
            join msg in db.Set<InboxMessageEntity>() on hs.MessageId equals msg.Id
            where hs.Id == handlerStatusId && hs.IsPoisoned
            select new { hs, msg }
        ).FirstOrDefaultAsync(ct);

        if (result is null) return null;

        var props = SafeDeserializeToJsonElement(result.msg.SerializedProperties);
        var msgType = ExtractType(result.msg.SerializedProperties);
        var (jsonPayload, base64) = DecodeContent(result.msg.Content);

        return new InboxPoisonedDetailDto(
            result.hs.Id, result.hs.MessageId, msgType, result.hs.HandlerKey, result.msg.ReceivedAt,
            result.hs.ErrorCount, result.hs.RequeuedCount,
            string.IsNullOrEmpty(result.hs.LastError) ? null : result.hs.LastError,
            props, jsonPayload, base64, DbContextName);
    }

    public async Task<InboxMessageHandlersDto?> GetInboxHandlersForMessageAsync(string messageId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        var msg = await db.Set<InboxMessageEntity>()
            .FirstOrDefaultAsync(x => x.Id == messageId, ct);
        if (msg is null) return null;

        var handlers = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.MessageId == messageId)
            .ToListAsync(ct);

        var msgType = ExtractType(msg.SerializedProperties);
        var summaries = handlers.Select(h => new InboxHandlerStatusSummaryDto(
            h.Id, h.HandlerKey, h.ErrorCount, h.RequeuedCount,
            string.IsNullOrEmpty(h.LastError) ? null : h.LastError,
            h.IsPoisoned, h.CompletedAt.HasValue, DbContextName)).ToList();

        return new InboxMessageHandlersDto(messageId, msgType, msg.ReceivedAt, summaries);
    }

    public async Task<IResult> RequeueInboxHandlerAsync(Guid handlerStatusId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await RequeueHelper.RequeueInboxHandlerAsync(db, handlerStatusId, ct);
    }

    public async Task<IResult> RequeueAllInboxHandlersForMessageAsync(string messageId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var handlers = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.MessageId == messageId && x.IsPoisoned)
            .ToListAsync(ct);

        if (handlers.Count == 0)
            return Results.NotFound($"No poisoned handlers found for message '{messageId}'.");

        foreach (var h in handlers)
            h.Requeue();

        try
        {
            await db.SaveChangesAsync(ct);
            return Results.Ok(handlers.Select(h => h.Id).ToList());
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict("One or more handler statuses were modified concurrently. Refresh and retry.");
        }
    }

    public async Task<IResult> DeleteInboxHandlerStatusAsync(Guid handlerStatusId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        var entity = await db.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == handlerStatusId, ct);

        if (entity is null) return Results.NotFound();
        if (!entity.IsPoisoned) return Results.BadRequest("Handler status is not poisoned.");

        var messageId = entity.MessageId;
        db.Set<InboxHandlerStatusEntity>().Remove(entity);
        await db.SaveChangesAsync(ct);

        // Cascade orphan cleanup: delete parent InboxMessageEntity if no handlers remain
        var remainingHandlers = await db.Set<InboxHandlerStatusEntity>()
            .AnyAsync(x => x.MessageId == messageId, ct);

        if (!remainingHandlers)
        {
            var parent = await db.Set<InboxMessageEntity>()
                .FindAsync([messageId], ct);
            if (parent is not null)
            {
                db.Set<InboxMessageEntity>().Remove(parent);
                await db.SaveChangesAsync(ct);
            }
        }

        return Results.Ok();
    }

    public async Task<BulkActionResult> BulkRequeueInboxAsync(List<Guid>? ids, bool all, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        if (all)
        {
            await db.Set<InboxHandlerStatusEntity>()
                .Where(x => x.IsPoisoned)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsPoisoned, false)
                    .SetProperty(x => x.ErrorCount, 0)
                    .SetProperty(x => x.LastError, string.Empty)
                    .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.ProcessingStartedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.RequeuedCount, x => x.RequeuedCount + 1)
                    .SetProperty(x => x.Version, x => x.Version + 1), ct);
            return new BulkActionResult([], []);
        }

        if (ids is null or { Count: 0 })
            return new BulkActionResult([], []);

        var succeeded = new List<Guid>();
        var failed = new List<BulkFailure>();
        foreach (var id in ids)
        {
            var result = await RequeueHelper.RequeueInboxHandlerAsync(db, id, ct);
            if (IsSuccessResult(result))
                succeeded.Add(id);
            else
                failed.Add(new BulkFailure(id, "Not found, not poisoned, or concurrent modification."));
        }
        return new BulkActionResult(succeeded, failed);
    }

    public async Task<IResult> BulkDeleteInboxAsync(List<Guid>? ids, bool all, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        if (all)
        {
            await db.Set<InboxHandlerStatusEntity>()
                .Where(x => x.IsPoisoned)
                .ExecuteDeleteAsync(ct);
            return Results.Ok();
        }

        if (ids is null or { Count: 0 }) return Results.Ok();

        await db.Set<InboxHandlerStatusEntity>()
            .Where(x => ids.Contains(x.Id) && x.IsPoisoned)
            .ExecuteDeleteAsync(ct);
        return Results.Ok();
    }

    // ─── Health ───────────────────────────────────────────────────────────────

    public Task<DbContextHealthDto> GetHealthAsync(CancellationToken ct)
    {
        _metricsState.ContextMetrics.TryGetValue(_contextKey, out var metrics);

        return Task.FromResult(new DbContextHealthDto(
            DbContextName,
            metrics.PoisonedOutboxCount,
            metrics.PoisonedInboxCount,
            metrics.PendingOutboxCount,
            metrics.PendingInboxCount,
            _outboxProcessor?.LastSuccessfulProcessingAt,
            _inboxProcessor?.LastSuccessfulProcessingAt));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string ExtractType(string serializedProperties)
    {
        try
        {
            using var doc = JsonDocument.Parse(serializedProperties);
            if (doc.RootElement.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "(unknown)";
        }
        catch { }
        return "(unknown)";
    }

    private static JsonElement SafeDeserializeToJsonElement(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static (string? JsonPayload, string PayloadBase64) DecodeContent(byte[] content)
    {
        var base64 = Convert.ToBase64String(content);
        try
        {
            var text = Encoding.UTF8.GetString(content);
            JsonDocument.Parse(text);
            return (text, base64);
        }
        catch
        {
            return (null, base64);
        }
    }

    private static bool IsSuccessResult(IResult result)
    {
        // Results.Ok() has StatusCode 200; check via interface
        if (result is IStatusCodeHttpResult sc)
            return sc.StatusCode is null or (>= 200 and < 300);
        return true;
    }
}
