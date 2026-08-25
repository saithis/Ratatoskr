namespace Ratatoskr.Core;

/// <summary>
/// Handles batches of messages of a specific type.
/// </summary>
/// <typeparam name="TMessage">The message type to handle</typeparam>
public interface IBatchMessageHandler<TMessage>
    where TMessage : notnull
{
    /// <summary>
    /// Handles a batch of messages.
    /// </summary>
    /// <param name="messages">The list of deserialized messages in the batch</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public Task HandleAsync(
        IReadOnlyList<TMessage> messages,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Container for a consumed message payload and its delivery properties.
/// </summary>
/// <typeparam name="TMessage">The message payload type</typeparam>
/// <param name="Message">The deserialized message payload</param>
/// <param name="Properties">Context about the message delivery</param>
public record ConsumedMessage<TMessage>(TMessage Message, MessageProperties Properties)
    where TMessage : notnull;
