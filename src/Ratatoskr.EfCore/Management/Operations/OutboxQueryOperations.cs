using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Internal;

namespace Ratatoskr.Management.EfCore.Operations;

/// <summary>Returns one keyset page of outbox rows.</summary>
internal sealed class ListOutboxOperation(
    ManagementResourceResolver resolver,
    ILogger<ListOutboxOperation> logger
) : IManagementOperation
{
    public string Name => ManagementOperationNames.OutboxList;

    public Type RequestType => typeof(ListMessagesRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<ListMessagesRequest>();

        if (resolver.Resolve(context, DurabilityFeature.Outbox, out var db) is { } failure)
        {
            return failure;
        }

        if (MessageQueries.Validate(request.Filter, requireNonEmpty: false) is { } invalid)
        {
            return invalid;
        }

        if (!ManagementCursor.TryDecode(request.Cursor, out var cursor))
        {
            return ManagementResult.Invalid(
                "The pagination cursor is not one this service issued.",
                ManagementErrorCodes.InvalidCursor
            );
        }

        var limit = ManagementPaging.ClampPageSize(request.Limit);

        // Fetch one more than asked for: that answers "is there another page" without a second
        // round-trip, and without the full-table count the old endpoint paid for on every page.
        var rows = await MessageQueries
            .After(MessageQueries.Outbox(db, request.Filter), cursor)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(limit + 1)
            .Select(x => new
            {
                x.Id,
                x.SerializedProperties,
                x.TransportName,
                x.CreatedAt,
                x.ProcessedAt,
                x.FailedAt,
                x.ScheduledAt,
                x.IsPoisoned,
                x.ErrorCount,
                x.RequeuedCount,
                x.Error,
            })
            .ToListAsync(cancellationToken);

        var hasNext = rows.Count > limit;
        if (hasNext)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.ConvertAll(row => new OutboxListItem(
            row.Id,
            ManagementPayloadDecoder.ExtractType(row.SerializedProperties, logger),
            row.TransportName,
            row.CreatedAt,
            row.ProcessedAt,
            row.FailedAt,
            row.ScheduledAt,
            row.IsPoisoned,
            row.ErrorCount,
            row.RequeuedCount,
            string.IsNullOrEmpty(row.Error) ? null : row.Error
        ));

        var next = hasNext
            ? new ManagementCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;

        return ManagementResult.Ok(new CursorPage<OutboxListItem>(items, next));
    }
}

/// <summary>Counts the outbox rows a filter matches. Backs the destructive-action preview.</summary>
internal sealed class CountOutboxOperation(ManagementResourceResolver resolver) : IManagementOperation
{
    public string Name => ManagementOperationNames.OutboxCount;

    public Type RequestType => typeof(CountMessagesRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<CountMessagesRequest>();

        if (resolver.Resolve(context, DurabilityFeature.Outbox, out var db) is { } failure)
        {
            return failure;
        }

        if (MessageQueries.Validate(request.Filter, requireNonEmpty: false) is { } invalid)
        {
            return invalid;
        }

        var count = await MessageQueries
            .Outbox(db, request.Filter)
            .LongCountAsync(cancellationToken);

        return ManagementResult.Ok(new MessageCountResponse(count));
    }
}

/// <summary>Returns one outbox row with its payload and CloudEvents metadata.</summary>
internal sealed class GetOutboxOperation(
    ManagementResourceResolver resolver,
    ILogger<GetOutboxOperation> logger
) : IManagementOperation
{
    public string Name => ManagementOperationNames.OutboxGet;

    public Type RequestType => typeof(GetMessageRequest);

    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<GetMessageRequest>();

        if (resolver.Resolve(context, DurabilityFeature.Outbox, out var db) is { } failure)
        {
            return failure;
        }

        var entity = await db.Set<OutboxMessageEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (entity is null)
        {
            return ManagementResult.NotFound($"Outbox message '{request.Id}' was not found.");
        }

        var properties = entity.GetProperties();
        var (json, base64) = ManagementPayloadDecoder.DecodeContent(entity.Content, logger);

        return ManagementResult.Ok(
            new OutboxDetail(
                entity.Id,
                properties.Type ?? ManagementPayloadDecoder.UnknownType,
                entity.TransportName,
                entity.CreatedAt,
                entity.ProcessedAt,
                entity.FailedAt,
                entity.ScheduledAt,
                entity.IsPoisoned,
                entity.ErrorCount,
                entity.RequeuedCount,
                string.IsNullOrEmpty(entity.Error) ? null : entity.Error,
                ManagementPayloadDecoder.ToView(properties),
                json,
                base64
            )
        );
    }
}
