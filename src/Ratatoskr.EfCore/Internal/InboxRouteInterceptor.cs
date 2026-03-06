using Microsoft.EntityFrameworkCore;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Implements <see cref="IMessageRouteInterceptor"/> by delegating to <see cref="InboxAcceptor{TDbContext}"/>
/// to persist inbox-managed handler statuses before the message is dispatched.
/// </summary>
internal class InboxRouteInterceptor<TDbContext>(
    InboxAcceptor<TDbContext> inboxAcceptor) : IMessageRouteInterceptor
    where TDbContext : DbContext, IInboxDbContext
{
    public async Task<RouteInterceptResult> BeforeDispatchAsync(
        byte[] body, MessageProperties properties, string transportName,
        string channelName, CancellationToken cancellationToken)
    {
        var outcome = await inboxAcceptor.AcceptAsync(body, properties, transportName, channelName, cancellationToken);
        return new RouteInterceptResult(HandlersAccepted: outcome is InboxAcceptOutcome.Accepted or InboxAcceptOutcome.Duplicate);
    }
}
