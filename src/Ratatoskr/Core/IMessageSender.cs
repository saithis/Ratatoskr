namespace Ratatoskr.Core;

/// <summary>
/// Sends serialized message bytes to a transport (e.g. RabbitMQ, Azure Service Bus).
/// </summary>
public interface IMessageSender
{
    /// <summary>The name of the transport this sender targets.</summary>
    public string TransportName { get; }

    /// <summary>Sends a serialized message with the given properties over the transport.</summary>
    public Task SendAsync(
        byte[] content,
        MessageProperties props,
        CancellationToken cancellationToken
    );
}
