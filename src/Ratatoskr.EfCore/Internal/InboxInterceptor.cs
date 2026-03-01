using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Called by <see cref="MessageDispatcher"/> when a message arrives on a non-local transport
/// (e.g. RabbitMQ) and inbox-managed handlers are registered for the message type.
/// Creates its own DI scope for full isolation from handler scopes.
/// Persists the message and handler statuses to the database so that <see cref="InboxProcessor{TDbContext}"/>
/// can deliver them with per-handler retry and deduplication.
/// </summary>
internal class InboxInterceptor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<InboxInterceptor<TDbContext>> logger)
    : IInboxInterceptor
    where TDbContext : DbContext, IInboxDbContext
{
    public async Task AcceptAsync(
        byte[] body,
        MessageProperties properties,
        IReadOnlyList<InboxHandlerRegistration> managedHandlers,
        string transportName,
        CancellationToken cancellationToken)
    {
        await InboxPersistence.PersistAsync<TDbContext>(
            scopeFactory, properties.Id!, transportName,
            body, properties, managedHandlers, timeProvider,
            observers, inboxProcessor.TriggerAsync, logger, cancellationToken);
    }
}
