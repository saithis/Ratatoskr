using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Agent;

internal static class EfCoreManagementOperationHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Add(IServiceCollection services)
    {
        Add(services, "GetStats", static (operations, _, ct) => operations.BuildHeartbeatAsync(ct));
        Add(services, "GetOutbox", static (operations, request, ct) => operations.GetOutboxMessagesAsync(Context(request), Payload<GetOutboxMessagesRequest>(request) ?? new(), ct));
        Add(services, "GetOutboxDetail", static (operations, request, ct) => operations.GetOutboxDetailAsync(Context(request), Required(Payload<GetOutboxDetailRequest>(request)?.Id, "Id"), ct));
        Add(services, "RequeueOutbox", static (operations, request, ct) => operations.RequeueOutboxAsync(Context(request), Required(Payload<RequeueOutboxRequest>(request)?.Id, "Id"), ct));
        Add(services, "BulkRequeueOutbox", static (operations, request, ct) => operations.BulkRequeueOutboxAsync(Context(request), ct));
        Add(services, "DeleteOutbox", static (operations, request, ct) => operations.DeleteOutboxAsync(Context(request), Required(Payload<DeleteOutboxRequest>(request)?.Id, "Id"), ct));
        Add(services, "BulkDeleteOutbox", static (operations, request, ct) => operations.BulkDeleteOutboxAsync(Context(request), ct));
        Add(services, "GetInbox", static (operations, request, ct) => operations.GetInboxMessagesAsync(Context(request), Payload<GetInboxMessagesRequest>(request) ?? new(), ct));
        Add(services, "GetInboxDetail", static (operations, request, ct) => operations.GetInboxDetailAsync(Context(request), Required(Payload<GetInboxDetailRequest>(request)?.StatusId, "StatusId"), ct));
        Add(services, "RequeueInboxHandler", static (operations, request, ct) => operations.RequeueInboxHandlerAsync(Context(request), Required(Payload<RequeueInboxHandlerRequest>(request)?.StatusId, "StatusId"), ct));
        Add(services, "RequeueInboxMessage", static (operations, request, ct) => operations.RequeueInboxMessageAsync(Context(request), Required(Payload<RequeueInboxMessageRequest>(request)?.MessageId, "MessageId"), ct));
        Add(services, "BulkRequeueInbox", static (operations, request, ct) => operations.BulkRequeueInboxAsync(Context(request), ct));
        Add(services, "DeleteInboxHandler", static (operations, request, ct) => operations.DeleteInboxHandlerAsync(Context(request), Required(Payload<DeleteInboxHandlerRequest>(request)?.StatusId, "StatusId"), ct));
        Add(services, "BulkDeleteInbox", static (operations, request, ct) => operations.BulkDeleteInboxAsync(Context(request), ct));
    }

    private static void Add<T>(
        IServiceCollection services,
        string operation,
        Func<EfCoreManagementOperations, ManagementRequestEnvelope, CancellationToken, Task<T>> execute
    ) => services.AddSingleton<IManagementOperationHandler>(sp =>
        new DelegateManagementOperationHandler(operation, async (request, ct) =>
        {
            var result = await execute(sp.GetRequiredService<EfCoreManagementOperations>(), request, ct);
            return ManagementResponseEnvelope.Ok(
                request,
                new ManagementPayload("application/json", JsonSerializer.Serialize(result, JsonOptions))
            );
        })
    );

    private static string Context(ManagementRequestEnvelope request) =>
        request.TargetContext ?? throw new ArgumentException("Target context is required.", nameof(request));

    private static T Required<T>(T? value, string name) where T : struct =>
        value ?? throw new ArgumentException($"{name} is required.", name);

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} is required.", name)
            : value;

    private static T? Payload<T>(ManagementRequestEnvelope request) =>
        string.IsNullOrWhiteSpace(request.PayloadJson)
            ? default
            : JsonSerializer.Deserialize<T>(request.PayloadJson, JsonOptions);
}
