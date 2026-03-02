using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Single entry point for inbox persistence. Called by transport consumers (e.g.
/// <c>RabbitMqConsumer</c>) and <c>DurableLocalMessageSender</c> to persist
/// inbox-managed handler statuses to the database before message dispatch.
/// </summary>
internal class InboxAcceptor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    InboxHandlerRegistry inboxHandlerRegistry,
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<InboxAcceptor<TDbContext>> logger)
    : IInboxAcceptor
    where TDbContext : DbContext, IInboxDbContext
{
    public async Task<bool> AcceptAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        CancellationToken cancellationToken)
    {
        var inboxHandlers = properties.Type != null
            ? inboxHandlerRegistry.GetByWireTypeName(properties.Type)
            : [];

        if (inboxHandlers.Count == 0)
            return false;

        if (string.IsNullOrWhiteSpace(properties.Id))
            throw new InvalidOperationException("Inbox delivery requires MessageProperties.Id for deduplication.");

        await InboxPersistence.PersistAsync<TDbContext>(
            scopeFactory, properties.Id, transportName,
            body, properties, inboxHandlers, timeProvider,
            observers, inboxProcessor.TriggerAsync, logger, cancellationToken);

        return true;
    }
}
