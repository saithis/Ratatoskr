namespace Ratatoskr.Core;

/// <summary>
/// Handles messages of a specific type.
/// </summary>
/// <typeparam name="TMessage">The message type to handle</typeparam>
public interface IMessageHandler<in TMessage>
    where TMessage : notnull
{
    /// <summary>
    /// Handles the message.
    /// </summary>
    /// <param name="message">The deserialized message</param>
    /// <param name="properties">Context about the message delivery</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public Task HandleAsync(
        TMessage message,
        MessageProperties properties,
        CancellationToken cancellationToken
    );
}
