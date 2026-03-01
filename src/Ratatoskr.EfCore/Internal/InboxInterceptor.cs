using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Called by <see cref="MessageDispatcher"/> when a message arrives on a non-local transport
/// (e.g. RabbitMQ) and inbox-managed handlers are registered for the message type.
/// Persists the message and handler statuses to the database so that <see cref="InboxProcessor{TDbContext}"/>
/// can deliver them with per-handler retry and deduplication.
/// </summary>
internal class InboxInterceptor<TDbContext>(
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<InboxInterceptor<TDbContext>> logger)
    : IInboxInterceptor
    where TDbContext : DbContext, IInboxDbContext
{
    public async Task AcceptAsync(
        IServiceProvider scopedServices,
        byte[] body,
        MessageProperties properties,
        IReadOnlyList<InboxHandlerRegistration> managedHandlers,
        string transportName,
        CancellationToken cancellationToken)
    {
        var dbContext = scopedServices.GetRequiredService<TDbContext>();

        var persisted = await InboxPersistence.PersistAsync(
            dbContext, properties.Id!, transportName,
            body, properties, managedHandlers, timeProvider, logger, cancellationToken);

        if (!persisted)
            return; // Concurrent instance already persisted — nothing more to do.

        // Notify observers that the message has been accepted into the inbox
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(new MessageActivity
                {
                    Stage = MessageStage.InboxQueued,
                    Properties = properties,
                    SerializedBody = body,
                    TransportName = transportName,
                    Timestamp = timeProvider.GetUtcNow(),
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Observer failed at {Stage} stage", MessageStage.InboxQueued);
            }
        }

        await inboxProcessor.TriggerAsync(cancellationToken);
    }
}
