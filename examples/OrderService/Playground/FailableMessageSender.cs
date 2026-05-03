using Ratatoskr.Core;

namespace OrderService.Playground;

/// <summary>Wraps a transport sender to simulate intermittent failures for the playground.</summary>
internal sealed class FailableMessageSender(IMessageSender inner, OutboxFailureState state) : IMessageSender
{
    public string TransportName => inner.TransportName;

    public Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        if (state.TryConsumeSendFailure())
            throw new InvalidOperationException("Simulated transport send failure (playground outbox-failure toggle).");

        return inner.SendAsync(content, props, cancellationToken);
    }
}
