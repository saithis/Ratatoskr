using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.EfCore.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.Management.Agent;

/// <summary>
/// Executes management queries and operations against the local host's DbContexts and channels.
/// Used both by the RabbitMQ management consumer and by the in-process management transport.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "DbContext is managed and disposed by the IServiceScope.")]
public sealed class ManagementRequestHandler(
    IServiceProvider serviceProvider,
    ChannelRegistry channelRegistry,
    IOptions<RatatoskrManagementOptions> options,
    ILogger<ManagementRequestHandler> logger
)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ManagementResponseEnvelope> HandleAsync(
        ManagementRequestEnvelope request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request.Action switch
            {
                "GetStats" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(await BuildHeartbeatAsync(cancellationToken), JsonOptions)
                ),
                "GetOutbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await GetOutboxMessagesAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<GetOutboxMessagesRequest>(request.PayloadJson) ?? new GetOutboxMessagesRequest(),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "GetOutboxDetail" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await GetOutboxDetailAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<GetOutboxDetailRequest>(request.PayloadJson)?.Id ?? throw new ArgumentException("Id required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "RequeueOutbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await RequeueOutboxAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<RequeueOutboxRequest>(request.PayloadJson)?.Id ?? throw new ArgumentException("Id required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "BulkRequeueOutbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await BulkRequeueOutboxAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "DeleteOutbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await DeleteOutboxAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<DeleteOutboxRequest>(request.PayloadJson)?.Id ?? throw new ArgumentException("Id required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "BulkDeleteOutbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await BulkDeleteOutboxAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "GetInbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await GetInboxMessagesAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<GetInboxMessagesRequest>(request.PayloadJson) ?? new GetInboxMessagesRequest(),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "GetInboxDetail" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await GetInboxDetailAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<GetInboxDetailRequest>(request.PayloadJson)?.StatusId ?? throw new ArgumentException("StatusId required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "RequeueInboxHandler" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await RequeueInboxHandlerAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<RequeueInboxHandlerRequest>(request.PayloadJson)?.StatusId ?? throw new ArgumentException("StatusId required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "RequeueInboxMessage" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await RequeueInboxMessageAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<RequeueInboxMessageRequest>(request.PayloadJson)?.MessageId ?? throw new ArgumentException("MessageId required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "BulkRequeueInbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await BulkRequeueInboxAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "DeleteInboxHandler" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await DeleteInboxHandlerAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            Deserialize<DeleteInboxHandlerRequest>(request.PayloadJson)?.StatusId ?? throw new ArgumentException("StatusId required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                "BulkDeleteInbox" => ManagementResponseEnvelope.Ok(
                    request.RequestId,
                    JsonSerializer.Serialize(
                        await BulkDeleteInboxAsync(
                            request.TargetContext ?? throw new ArgumentException("TargetContext required", nameof(request)),
                            cancellationToken
                        ),
                        JsonOptions
                    )
                ),
                _ => ManagementResponseEnvelope.Error(request.RequestId, $"Unknown action '{request.Action}'")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle management request {Action} for {TargetService}", request.Action, request.TargetService);
            return ManagementResponseEnvelope.Error(request.RequestId, ex.Message);
        }
    }

    public async Task<ServiceHeartbeat> BuildHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        using var scope = serviceProvider.CreateScope();
        var descriptors = scope.ServiceProvider.GetServices<IEfCoreManagementDbContextDescriptor>().ToList();

        var dbSummaries = new List<DbContextSummaryDto>();

        foreach (var desc in descriptors)
        {
            var db = desc.GetDbContext(scope.ServiceProvider);
            long pendingOutbox = 0;
            long poisonedOutbox = 0;
            long pendingInbox = 0;
            long poisonedInbox = 0;

            if (desc.HasOutbox && db is IOutboxDbContext)
            {
                try
                {
                    pendingOutbox = await db.Set<OutboxMessageEntity>()
                        .LongCountAsync(x => x.ProcessedAt == null && !x.IsPoisoned, cancellationToken);
                    poisonedOutbox = await db.Set<OutboxMessageEntity>()
                        .LongCountAsync(x => x.IsPoisoned, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not query outbox metrics for {DbContext}", desc.DbContextName);
                }
            }

            if (desc.HasInbox && db is IInboxDbContext)
            {
                try
                {
                    pendingInbox = await db.Set<InboxHandlerStatusEntity>()
                        .LongCountAsync(x => x.CompletedAt == null && !x.IsPoisoned, cancellationToken);
                    poisonedInbox = await db.Set<InboxHandlerStatusEntity>()
                        .LongCountAsync(x => x.IsPoisoned, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not query inbox metrics for {DbContext}", desc.DbContextName);
                }
            }

            dbSummaries.Add(new DbContextSummaryDto
            {
                DbContextName = desc.DbContextName,
                HasOutbox = desc.HasOutbox,
                HasInbox = desc.HasInbox,
                PendingOutboxCount = pendingOutbox,
                PoisonedOutboxCount = poisonedOutbox,
                PendingInboxCount = pendingInbox,
                PoisonedInboxCount = poisonedInbox
            });
        }

        var channels = new List<ChannelSummaryDto>();
        var allChannels = channelRegistry.GetPublishChannels().Concat(channelRegistry.GetConsumeChannels());
        foreach (var ch in allChannels)
        {
            var rmqOpts = ch.GetRabbitMqChannelOptions();
            channels.Add(new ChannelSummaryDto
            {
                ChannelName = ch.ChannelName,
                ChannelType = ch.Intent.ToString(),
                TransportName = string.Join(", ", ch.Transports),
                MessageTypes = ch.Messages.Select(m => m.MessageTypeName).ToList(),
                QueueName = rmqOpts?.QueueName,
                ExchangeName = rmqOpts?.AmqpExchangeName ?? ch.ChannelName
            });
        }

        return new ServiceHeartbeat
        {
            ServiceName = opt.ServiceName,
            InstanceId = opt.InstanceId,
            MachineName = opt.MachineName,
            Environment = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            StartedAt = DateTimeOffset.UtcNow,
            Timestamp = DateTimeOffset.UtcNow,
            DbContexts = dbSummaries,
            Channels = channels
        };
    }

    public async Task<PagedResult<OutboxItemDto>> GetOutboxMessagesAsync(
        string contextName,
        GetOutboxMessagesRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        IQueryable<OutboxMessageEntity> query = db.Set<OutboxMessageEntity>().AsNoTracking();

        query = request.Status?.ToLowerInvariant() switch
        {
            "poisoned" => query.Where(x => x.IsPoisoned),
            "pending" => query.Where(x => x.ProcessedAt == null && !x.IsPoisoned),
            "processed" => query.Where(x => x.ProcessedAt != null),
            _ => query
        };

        var total = await query.CountAsync(cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new OutboxItemDto(
                x.Id,
                x.TransportName,
                x.CreatedAt,
                x.ProcessedAt,
                x.FailedAt,
                x.IsPoisoned,
                x.ErrorCount,
                x.Error,
                x.RequeuedCount,
                x.ScheduledAt
            ))
            .ToListAsync(cancellationToken);

        return new PagedResult<OutboxItemDto>(items, total, page, pageSize);
    }

    public async Task<OutboxDetailDto?> GetOutboxDetailAsync(
        string contextName,
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var entity = await db.Set<OutboxMessageEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var props = entity.GetProperties();
        var (json, _) = ManagementHelpers.DecodeContent(entity.Content, logger);

        return new OutboxDetailDto(
            entity.Id,
            entity.TransportName,
            entity.CreatedAt,
            entity.ProcessedAt,
            entity.FailedAt,
            entity.IsPoisoned,
            entity.ErrorCount,
            entity.Error,
            entity.RequeuedCount,
            entity.ScheduledAt,
            ToPropertiesDto(props),
            json ?? (entity.Content != null ? Encoding.UTF8.GetString(entity.Content) : null)
        );
    }

    public async Task<RequeueResultDto> RequeueOutboxAsync(
        string contextName,
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var entity = await db.Set<OutboxMessageEntity>()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            throw new InvalidOperationException($"Outbox message '{id}' not found.");
        }

        entity.Requeue();
        await db.SaveChangesAsync(cancellationToken);
        return new RequeueResultDto(1);
    }

    public async Task<RequeueResultDto> BulkRequeueOutboxAsync(
        string contextName,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var entities = await db.Set<OutboxMessageEntity>()
            .Where(x => x.IsPoisoned)
            .ToListAsync(cancellationToken);

        foreach (var entity in entities)
        {
            entity.Requeue();
        }

        await db.SaveChangesAsync(cancellationToken);
        return new RequeueResultDto(entities.Count);
    }

    public async Task<DeleteResultDto> DeleteOutboxAsync(
        string contextName,
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var entity = await db.Set<OutboxMessageEntity>()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return new DeleteResultDto(0);
        }

        db.Set<OutboxMessageEntity>().Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new DeleteResultDto(1);
    }

    public async Task<DeleteResultDto> BulkDeleteOutboxAsync(
        string contextName,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var entities = await db.Set<OutboxMessageEntity>()
            .Where(x => x.IsPoisoned)
            .ToListAsync(cancellationToken);

        db.Set<OutboxMessageEntity>().RemoveRange(entities);
        await db.SaveChangesAsync(cancellationToken);
        return new DeleteResultDto(entities.Count);
    }

    public async Task<PagedResult<InboxItemDto>> GetInboxMessagesAsync(
        string contextName,
        GetInboxMessagesRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        IQueryable<InboxHandlerStatusEntity> statusQuery = db.Set<InboxHandlerStatusEntity>().AsNoTracking();

        statusQuery = request.Status?.ToLowerInvariant() switch
        {
            "poisoned" => statusQuery.Where(x => x.IsPoisoned),
            "pending" => statusQuery.Where(x => x.CompletedAt == null && !x.IsPoisoned),
            "completed" => statusQuery.Where(x => x.CompletedAt != null),
            _ => statusQuery
        };

        var total = await statusQuery.CountAsync(cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var query = from s in statusQuery
                    join m in db.Set<InboxMessageEntity>().AsNoTracking() on s.MessageId equals m.Id into msgGroup
                    from msg in msgGroup.DefaultIfEmpty()
                    orderby s.CreatedAt descending
                    select new InboxItemDto(
                        s.Id,
                        s.MessageId,
                        s.HandlerKey,
                        s.CreatedAt,
                        s.CompletedAt,
                        s.IsPoisoned,
                        (short)s.ErrorCount,
                        s.LastError,
                        s.RequeuedCount,
                        msg != null ? msg.TransportName : "Unknown"
                    );

        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<InboxItemDto>(items, total, page, pageSize);
    }

    public async Task<InboxDetailDto?> GetInboxDetailAsync(
        string contextName,
        Guid statusId,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var status = await db.Set<InboxHandlerStatusEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == statusId, cancellationToken);

        if (status is null)
        {
            return null;
        }

        var message = await db.Set<InboxMessageEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == status.MessageId, cancellationToken);

        var otherStatuses = await db.Set<InboxHandlerStatusEntity>()
            .AsNoTracking()
            .Where(x => x.MessageId == status.MessageId && x.Id != statusId)
            .Select(x => new InboxOtherHandlerDto(
                x.Id,
                x.HandlerKey,
                x.IsPoisoned,
                x.CompletedAt,
                (short)x.ErrorCount,
                x.LastError
            ))
            .ToListAsync(cancellationToken);

        MessageProperties? props = null;
        string? contentStr = null;
        if (message != null)
        {
            props = message.GetProperties();
            var (json, _) = ManagementHelpers.DecodeContent(message.Content, logger);
            contentStr = json ?? (message.Content != null ? Encoding.UTF8.GetString(message.Content) : null);
        }

        return new InboxDetailDto(
            status.Id,
            status.MessageId,
            status.HandlerKey,
            status.CreatedAt,
            status.CompletedAt,
            status.IsPoisoned,
            (short)status.ErrorCount,
            status.LastError,
            status.RequeuedCount,
            message?.TransportName ?? "Unknown",
            props != null ? ToPropertiesDto(props) : null,
            contentStr,
            otherStatuses
        );
    }

    public async Task<RequeueResultDto> RequeueInboxHandlerAsync(
        string contextName,
        Guid statusId,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var status = await db.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == statusId, cancellationToken);

        if (status is null)
        {
            throw new InvalidOperationException($"Inbox handler status '{statusId}' not found.");
        }

        status.Requeue();
        await db.SaveChangesAsync(cancellationToken);
        return new RequeueResultDto(1);
    }

    public async Task<RequeueResultDto> RequeueInboxMessageAsync(
        string contextName,
        string messageId,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var statuses = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.MessageId == messageId && x.IsPoisoned)
            .ToListAsync(cancellationToken);

        foreach (var status in statuses)
        {
            status.Requeue();
        }

        await db.SaveChangesAsync(cancellationToken);
        return new RequeueResultDto(statuses.Count);
    }

    public async Task<RequeueResultDto> BulkRequeueInboxAsync(
        string contextName,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var statuses = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.IsPoisoned)
            .ToListAsync(cancellationToken);

        foreach (var s in statuses)
        {
            s.Requeue();
        }

        await db.SaveChangesAsync(cancellationToken);
        return new RequeueResultDto(statuses.Count);
    }

    public async Task<DeleteResultDto> DeleteInboxHandlerAsync(
        string contextName,
        Guid statusId,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var status = await db.Set<InboxHandlerStatusEntity>()
            .SingleOrDefaultAsync(x => x.Id == statusId, cancellationToken);

        if (status is null)
        {
            return new DeleteResultDto(0);
        }

        var messageId = status.MessageId;
        db.Set<InboxHandlerStatusEntity>().Remove(status);

        // If no other handlers exist for this message, delete the InboxMessageEntity
        var remainingCount = await db.Set<InboxHandlerStatusEntity>()
            .CountAsync(x => x.MessageId == messageId && x.Id != statusId, cancellationToken);

        if (remainingCount == 0)
        {
            var message = await db.Set<InboxMessageEntity>()
                .SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
            if (message != null)
            {
                db.Set<InboxMessageEntity>().Remove(message);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new DeleteResultDto(1);
    }

    public async Task<DeleteResultDto> BulkDeleteInboxAsync(
        string contextName,
        CancellationToken cancellationToken = default
    )
    {
        using var scope = serviceProvider.CreateScope();
        var db = GetDbContext(scope.ServiceProvider, contextName);

        var statuses = await db.Set<InboxHandlerStatusEntity>()
            .Where(x => x.IsPoisoned)
            .ToListAsync(cancellationToken);

        db.Set<InboxHandlerStatusEntity>().RemoveRange(statuses);
        await db.SaveChangesAsync(cancellationToken);
        return new DeleteResultDto(statuses.Count);
    }

    private static DbContext GetDbContext(IServiceProvider sp, string contextName)
    {
        var descriptors = sp.GetServices<IEfCoreManagementDbContextDescriptor>();
        var match = descriptors.FirstOrDefault(d => string.Equals(d.DbContextName, contextName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"DbContext '{contextName}' is not registered.");

        return match.GetDbContext(sp);
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static MessagePropertiesDto ToPropertiesDto(MessageProperties props) =>
        new(
            props.Id,
            props.Type,
            props.Source,
            props.Subject,
            props.DataSchema,
            props.ContentType,
            props.Time,
            props.ScheduledAt,
            props.TraceParent
        );
}
