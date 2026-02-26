namespace Ratatoskr.Core;

public interface IMessageSender
{
    string TransportName { get; }
    Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken);
}