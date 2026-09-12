using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.Http;

/// <summary>
/// How an HTTP request is turned into a management operation. The per-service API dispatches
/// locally; the dashboard dispatches through a transport to a remote service. Everything else
/// about the two surfaces — routes, DTOs, error mapping — is shared.
/// </summary>
public interface IManagementDispatchStrategy
{
    /// <summary>
    /// Executes <paramref name="operation"/> with <paramref name="request"/> as its payload.
    /// Implementations read whatever extra route values they need — transport, service, instance,
    /// context — from <paramref name="http"/>.
    /// </summary>
    Task<ManagementResponseEnvelope> ExecuteAsync(
        HttpContext http,
        string operation,
        object request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// The one management route tree. Both HTTP surfaces are produced from this method, so an
/// operation added here appears on both at once and the two cannot drift apart again.
/// </summary>
public static class ManagementApiRoutes
{
    /// <summary>The header a caller sets to make a mutating request idempotent across retries.</summary>
    public const string OperationIdHeader = "X-Ratatoskr-Operation-Id";

    /// <summary>
    /// Maps every management route under <paramref name="group"/>. The caller mounts the group
    /// wherever its surface lives: <c>/ratatoskr/api/v1</c> for the per-service API, or
    /// <c>/ratatoskr/api/transports/{transport}/services/{service}</c> for the dashboard.
    /// </summary>
    public static RouteGroupBuilder Map(
        RouteGroupBuilder group,
        IManagementDispatchStrategy strategy,
        ManagementApiPolicies policies
    )
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(policies);

        // Every handler takes more than an HttpContext. A minimal-API lambda whose only parameter
        // is HttpContext binds as a RequestDelegate instead of a route handler, and its IResult is
        // then silently discarded — the caller gets 200 with an empty body.
        group.MapGet(
            "/describe",
            (HttpContext http, CancellationToken cancellationToken) =>
                RunAsync(
                    http,
                    strategy,
                    ManagementOperationNames.ServiceDescribe,
                    new DescribeServiceRequest(),
                    cancellationToken
                )
        );

        group.MapGet(
            "/contexts",
            (HttpContext http, CancellationToken cancellationToken) =>
                RunAsync(
                    http,
                    strategy,
                    ManagementOperationNames.ContextsList,
                    new ListContextsRequest(),
                    cancellationToken
                )
        );

        var context = group.MapGroup("/contexts/{contextName}");

        context.MapGet(
            "/health",
            (HttpContext http, CancellationToken cancellationToken) =>
                RunAsync(
                    http,
                    strategy,
                    ManagementOperationNames.ContextHealth,
                    new ContextHealthRequest(),
                    cancellationToken
                )
        );

        MapMessageRoutes(
            context.MapGroup("/outbox"),
            strategy,
            policies,
            new MessageRouteOperations(
                List: ManagementOperationNames.OutboxList,
                Count: ManagementOperationNames.OutboxCount,
                Get: ManagementOperationNames.OutboxGet,
                Requeue: ManagementOperationNames.OutboxRequeue,
                Delete: ManagementOperationNames.OutboxDelete,
                RequeueMatching: ManagementOperationNames.OutboxRequeueMatching,
                DeleteMatching: ManagementOperationNames.OutboxDeleteMatching
            )
        );

        var inbox = context.MapGroup("/inbox");
        MapMessageRoutes(
            inbox,
            strategy,
            policies,
            new MessageRouteOperations(
                List: ManagementOperationNames.InboxList,
                Count: ManagementOperationNames.InboxCount,
                Get: ManagementOperationNames.InboxGet,
                Requeue: ManagementOperationNames.InboxRequeue,
                Delete: ManagementOperationNames.InboxDelete,
                RequeueMatching: ManagementOperationNames.InboxRequeueMatching,
                DeleteMatching: ManagementOperationNames.InboxDeleteMatching
            )
        );

        inbox
            .MapPost(
                "/messages/{messageId}/requeue",
                (HttpContext http, string messageId, CancellationToken cancellationToken) =>
                    RunAsync(
                        http,
                        strategy,
                        ManagementOperationNames.InboxRequeueMessage,
                        new RequeueInboxMessageRequest(messageId),
                        cancellationToken
                    )
            )
            .RequireAuthorization(policies.RequeueMessages)
            .AddEndpointFilter<ManagementAntiforgeryFilter>();

        return group;
    }

    private static void MapMessageRoutes(
        RouteGroupBuilder group,
        IManagementDispatchStrategy strategy,
        ManagementApiPolicies policies,
        MessageRouteOperations operations
    )
    {
        group.MapGet(
            "/",
            (
                HttpContext http,
                [AsParameters] MessageFilterQuery filter,
                string? cursor,
                int? limit,
                CancellationToken cancellationToken
            ) =>
                RunAsync(
                    http,
                    strategy,
                    operations.List,
                    new ListMessagesRequest
                    {
                        Filter = filter.ToFilter(),
                        Cursor = cursor,
                        Limit = limit ?? ManagementPaging.DefaultPageSize,
                    },
                    cancellationToken
                )
        );

        group.MapGet(
            "/count",
            (
                HttpContext http,
                [AsParameters] MessageFilterQuery filter,
                CancellationToken cancellationToken
            ) =>
                RunAsync(
                    http,
                    strategy,
                    operations.Count,
                    new CountMessagesRequest { Filter = filter.ToFilter() },
                    cancellationToken
                )
        );

        group
            .MapGet(
                "/{id:guid}",
                (HttpContext http, Guid id, CancellationToken cancellationToken) =>
                    RunAsync(http, strategy, operations.Get, new GetMessageRequest(id), cancellationToken)
            )
            .RequireAuthorization(policies.ViewPayloads);

        // Mutations are POST even where DELETE would read more naturally. The id list travels in
        // the body, and HTTP intermediaries are entitled to strip a DELETE body — which would
        // silently turn "delete these five" into a request with no ids at all.
        group
            .MapPost(
                "/requeue",
                (HttpContext http, MutateByIdsRequest request, CancellationToken cancellationToken) =>
                    RunAsync(http, strategy, operations.Requeue, request, cancellationToken)
            )
            .RequireAuthorization(policies.RequeueMessages)
            .AddEndpointFilter<ManagementAntiforgeryFilter>();

        group
            .MapPost(
                "/delete",
                (HttpContext http, MutateByIdsRequest request, CancellationToken cancellationToken) =>
                    RunAsync(http, strategy, operations.Delete, request, cancellationToken)
            )
            .RequireAuthorization(policies.DeleteMessages)
            .AddEndpointFilter<ManagementAntiforgeryFilter>();

        group
            .MapPost(
                "/requeue-matching",
                (HttpContext http, MutateMatchingRequest request, CancellationToken cancellationToken) =>
                    RunAsync(http, strategy, operations.RequeueMatching, request, cancellationToken)
            )
            .RequireAuthorization(policies.BulkOperations)
            .AddEndpointFilter<ManagementAntiforgeryFilter>();

        group
            .MapPost(
                "/delete-matching",
                (HttpContext http, MutateMatchingRequest request, CancellationToken cancellationToken) =>
                    RunAsync(http, strategy, operations.DeleteMatching, request, cancellationToken)
            )
            .RequireAuthorization(policies.BulkOperations)
            .AddEndpointFilter<ManagementAntiforgeryFilter>();
    }

    private static async Task<IResult> RunAsync(
        HttpContext http,
        IManagementDispatchStrategy strategy,
        string operation,
        object request,
        CancellationToken cancellationToken
    )
    {
        var response = await strategy.ExecuteAsync(http, operation, request, cancellationToken);

        if (!response.IsSuccess)
        {
            return ManagementProblemDetails.From(response);
        }

        return response.Payload is { } payload
            ? Results.Json(payload, ManagementJson.Options)
            : Results.NoContent();
    }

    private sealed record MessageRouteOperations(
        string List,
        string Count,
        string Get,
        string Requeue,
        string Delete,
        string RequeueMatching,
        string DeleteMatching
    );
}

/// <summary>The query-string form of a <see cref="MessageFilter"/>.</summary>
public sealed record MessageFilterQuery
{
    /// <summary>Which lifecycle state to include. Defaults to poisoned.</summary>
    [FromQuery]
    public MessageStatusFilter? Status { get; init; }

    /// <summary>Only rows created at or after this instant.</summary>
    [FromQuery]
    public DateTimeOffset? From { get; init; }

    /// <summary>Only rows created at or before this instant.</summary>
    [FromQuery]
    public DateTimeOffset? To { get; init; }

    /// <summary>A substring match over the row's serialized CloudEvents properties.</summary>
    [FromQuery]
    public string? Search { get; init; }

    /// <summary>Converts to the wire filter.</summary>
    public MessageFilter ToFilter() =>
        new()
        {
            Status = Status ?? MessageStatusFilter.Poisoned,
            From = From,
            To = To,
            Search = Search,
        };
}
