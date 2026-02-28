namespace Ratatoskr.Core;

/// <summary>
/// Called by <see cref="MessageDispatcher"/> when a message arrives on any transport
/// and inbox-managed handlers are registered for that message type.
/// The implementation (in Ratatoskr.EfCore) persists the message and handler statuses
/// to the database so the inbox processor can deliver them with retry.
/// </summary>
public interface IInboxInterceptor
{
    /// <summary>
    /// Accepts the message into the inbox.
    /// Must write <paramref name="managedHandlers"/> to durable storage and return
    /// before the dispatcher skips those handlers for synchronous invocation.
    /// </summary>
    Task AcceptAsync(
        IServiceProvider scopedServices,
        byte[] body,
        MessageProperties properties,
        IReadOnlyList<InboxHandlerRegistration> managedHandlers,
        CancellationToken cancellationToken);
}
