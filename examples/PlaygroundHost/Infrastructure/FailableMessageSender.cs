using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

internal sealed class FailableMessageSender(IMessageSender inner, OutboxFailureState state) : IMessageSender
{
    public string TransportName => inner.TransportName;

    public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        if (state.TryConsumeSendFailure(props))
            throw new InvalidOperationException("Simulated transport send failure (playground outbox-failure toggle).");

        return inner.SendAsync(content, props, cancellationToken);
    }
}
