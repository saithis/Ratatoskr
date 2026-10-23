using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.EfCore.Management.Endpoints;
using Ratatoskr.EfCore.Management.Endpoints.Inbox;
using Ratatoskr.EfCore.Management.Endpoints.Outbox;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Registers Ratatoskr EF Core management API endpoints.
/// Each endpoint targets a single DbContext identified by the <c>{contextName}</c> route segment.
/// The frontend first calls <c>GET /contexts</c> to discover available contexts,
/// then calls per-context endpoints as needed.
/// </summary>
internal sealed class EfCoreEndpointConfigurator : IRatatoskrEndpointConfigurator
{
    public void MapEndpoints(IEndpointRouteBuilder group)
    {
        var efcoreGroup = group.MapGroup("/efcore");

        GetContextsEndpoint.Map(efcoreGroup);

        var contextGroup = efcoreGroup.MapGroup("/contexts/{contextName}");
        ContextHealthEndpoint.Map(contextGroup);

        var outboxGroup = contextGroup.MapGroup("/outbox");
        ListPoisonedOutboxEndpoint.Map(outboxGroup);
        GetOutboxDetailEndpoint.Map(outboxGroup);
        RequeueOutboxEndpoint.Map(outboxGroup);
        DeleteOutboxEndpoint.Map(outboxGroup);
        BulkRequeueOutboxEndpoint.Map(outboxGroup);
        BulkDeleteOutboxEndpoint.Map(outboxGroup);

        var inboxGroup = contextGroup.MapGroup("/inbox");
        ListPoisonedInboxEndpoint.Map(inboxGroup);
        GetInboxHandlerDetailEndpoint.Map(inboxGroup);
        GetInboxMessageHandlersEndpoint.Map(inboxGroup);
        RequeueInboxHandlerEndpoint.Map(inboxGroup);
        RequeueInboxMessageEndpoint.Map(inboxGroup);
        DeleteInboxHandlerEndpoint.Map(inboxGroup);
        BulkRequeueInboxEndpoint.Map(inboxGroup);
        BulkDeleteInboxEndpoint.Map(inboxGroup);
    }
}
