using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

internal sealed class FailableMessageSender(IMessageSender inner, OutboxSendFailureRegistry registry) : IMessageSender
{
    public string TransportName => inner.TransportName;

    public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        if (registry.TryConsumeSendFailure(props))
            throw new InvalidOperationException("Simulated transport send failure (playground per-run policy).");

        return inner.SendAsync(content, props, cancellationToken);
    }
}
