using System.Text.Json;

namespace Ratatoskr.EfCore.Management.Dto;

internal record OutboxPoisonedListItemDto(
    Guid Id,
    string MessageType,
    DateTimeOffset CreatedAt,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    string DbContext);

internal record OutboxPoisonedDetailDto(
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

internal record PaginatedResponse<T>(
    List<T> Items,
    long TotalCount,
    string? NextCursor);
