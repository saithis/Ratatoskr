namespace Ratatoskr.Core;

public interface IMessageSender
{
    public string TransportName { get; }
    public Task SendAsync(
        byte[] content,
        MessageProperties props,
        CancellationToken cancellationToken
    );
}
