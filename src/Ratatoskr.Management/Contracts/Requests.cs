namespace Ratatoskr.Management.Contracts;

public sealed record GetOutboxMessagesRequest(string? Status = "Poisoned", int Page = 1, int PageSize = 20);
public sealed record GetOutboxDetailRequest(Guid Id);
public sealed record RequeueOutboxRequest(Guid Id);
public sealed record DeleteOutboxRequest(Guid Id);
public sealed record BulkRequeueOutboxRequest;
public sealed record BulkDeleteOutboxRequest;

public sealed record GetInboxMessagesRequest(string? Status = "Poisoned", int Page = 1, int PageSize = 20);
public sealed record GetInboxDetailRequest(Guid StatusId);
public sealed record RequeueInboxHandlerRequest(Guid StatusId);
public sealed record RequeueInboxMessageRequest(string MessageId);
public sealed record DeleteInboxHandlerRequest(Guid StatusId);
public sealed record BulkRequeueInboxRequest;
public sealed record BulkDeleteInboxRequest;
