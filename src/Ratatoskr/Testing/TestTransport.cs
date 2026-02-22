using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Test transport that captures all sent messages and optionally routes them
/// to registered handlers in-process. Replaces <see cref="IMessageSender"/>
/// when the test transport is active.
/// </summary>
internal class TestTransport(
    MessageSink sink,
    MessageDispatcher? dispatcher,
    TestTransportOptions options) : IMessageSender
{
    public async Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        // Inject session ID if active and not already present
        var sessionId = TestSessionContext.CurrentSessionId;
        if (sessionId != null && !props.Headers.ContainsKey(TestSessionContext.SessionHeaderName))
        {
            props.Headers[TestSessionContext.SessionHeaderName] = sessionId;
        }

        // 1. Always capture via the sink
        await ((IMessageSender)sink).SendAsync(content, props, cancellationToken);

        // 2. Optionally route to handlers in-process
        if (options.RouteMessages && dispatcher != null)
        {
            await dispatcher.DispatchAsync(content, props, cancellationToken);
        }
    }
}
