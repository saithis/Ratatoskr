namespace Ratatoskr.Core;

/// <summary>
/// Called by transport consumers before dispatching a message through <see cref="MessageDispatcher"/>.
/// If inbox-managed handlers are registered for the message type, the implementation persists the
/// message and handler statuses to durable storage so the inbox processor can deliver them with
/// per-handler retry and deduplication.
/// </summary>
public interface IInboxAcceptor
{
    /// <summary>
    /// Accepts inbox-managed handlers for the given message into durable storage.
    /// </summary>
    /// <returns>
    /// <c>true</c> if one or more inbox handlers were persisted;
    /// <c>false</c> if no inbox handlers are registered for this message type.
    /// </returns>
    Task<bool> AcceptAsync(byte[] body, MessageProperties properties, string transportName, CancellationToken cancellationToken);
}
