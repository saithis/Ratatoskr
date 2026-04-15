using System.Text.Json;

namespace Ratatoskr.EfCore.Management.Dto;

internal record InboxPoisonedListItemDto(
    Guid HandlerStatusId,
    string MessageId,
    string MessageType,
    string HandlerKey,
    DateTimeOffset ReceivedAt,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    string DbContext);

internal record InboxPoisonedDetailDto(
    Guid HandlerStatusId,
    string MessageId,
    string MessageType,
    string HandlerKey,
    DateTimeOffset ReceivedAt,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    JsonElement Properties,
    string? JsonPayload,
    string PayloadBase64,
    string DbContext);

internal record InboxMessageHandlersDto(
    string MessageId,
    string MessageType,
    DateTimeOffset ReceivedAt,
    List<InboxHandlerStatusSummaryDto> Handlers);

internal record InboxHandlerStatusSummaryDto(
    Guid HandlerStatusId,
    string HandlerKey,
    int ErrorCount,
    int RequeuedCount,
    string? LastError,
    bool IsPoisoned,
    bool IsCompleted,
    string DbContext);
