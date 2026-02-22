using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// A scoped test session that provides parallel-safe message tracking.
/// Each session has a unique ID that tags all messages published within its context,
/// allowing multiple tests to share the same application host without interference.
/// </summary>
/// <example>
/// <code>
/// await using var session = harness.CreateSession();
///
/// // Simulate receiving a message (dispatches to handlers)
/// await session.SimulateReceiveAsync(new OrderCreated { OrderId = "123" });
///
/// // Assert on messages sent within this session only
/// session.Sent.ShouldContain&lt;NotificationSent&gt;(m => m.OrderId == "123");
/// </code>
/// </example>
public class TestSession : IAsyncDisposable
{
    private readonly MessageSink _globalSink;
    private readonly MessageDispatcher _dispatcher;
    private readonly IMessageSerializer _serializer;
    private readonly IMessagePropertiesEnricher _enricher;
    private readonly IServiceProvider _serviceProvider;

    internal TestSession(
        MessageSink globalSink,
        IServiceProvider serviceProvider,
        MessageDispatcher dispatcher,
        IMessageSerializer serializer,
        IMessagePropertiesEnricher enricher)
    {
        _globalSink = globalSink;
        _serviceProvider = serviceProvider;
        _dispatcher = dispatcher;
        _serializer = serializer;
        _enricher = enricher;

        SessionId = Guid.NewGuid().ToString("N");
        Sent = new MessageSinkView(globalSink, SessionId);
    }

    /// <summary>
    /// The unique identifier for this test session.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// A filtered view of sent messages belonging to this session.
    /// Use assertion extensions like <c>ShouldContain&lt;T&gt;()</c> to verify messages.
    /// </summary>
    public MessageSinkView Sent { get; }

    /// <summary>
    /// Simulates receiving a message of the specified type.
    /// The message is dispatched to registered handlers within this session's context,
    /// so any outgoing messages published by the handlers are tagged with this session's ID.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message.</typeparam>
    /// <param name="message">The message content.</param>
    /// <param name="properties">Optional message properties. If not provided, properties will be enriched automatically.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The dispatch result indicating how the message was handled.</returns>
    public async Task<DispatchResult> SimulateReceiveAsync<TMessage>(
        TMessage message,
        MessageProperties? properties = null,
        CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var previousSessionId = TestSessionContext.CurrentSessionId;
        TestSessionContext.CurrentSessionId = SessionId;

        try
        {
            // Enrich properties
            properties = _enricher.Enrich<TMessage>(properties);

            // Serialize
            var body = _serializer.Serialize(message);
            properties.ContentType = _serializer.ContentType;

            // Dispatch to handlers
            var result = await _dispatcher.DispatchAsync(body, properties, cancellationToken);

            return result switch
            {
                DispatchResult.NoHandlers => throw new RatatoskrTestException(
                    $"No handlers found for message type '{typeof(TMessage).Name}' " +
                    $"(CloudEvents type: '{properties.Type}'). " +
                    "Ensure the handler is registered with AddHandler<TMessage, THandler>() " +
                    "and a consume channel is configured with Consumes<TMessage>()."),
                DispatchResult.PermanentError => throw new RatatoskrTestException(
                    $"Permanent error dispatching message type '{typeof(TMessage).Name}' " +
                    $"(CloudEvents type: '{properties.Type}'). " +
                    "Check logs for deserialization or type resolution errors."),
                _ => result
            };
        }
        finally
        {
            TestSessionContext.CurrentSessionId = previousSessionId;
        }
    }

    /// <summary>
    /// Creates a DI scope with this session's context active.
    /// Services resolved within this scope will have the session ID set,
    /// so any messages they publish are tagged with this session.
    /// </summary>
    public TestSessionScope CreateScope()
    {
        var scope = _serviceProvider.CreateScope();
        return new TestSessionScope(scope, SessionId);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Sent.CancelWaiters();
        TestSessionContext.CurrentSessionId = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A DI scope tied to a test session. Sets the session context
/// on creation so any messages published within this scope are tagged.
/// </summary>
public class TestSessionScope : IDisposable
{
    private readonly IServiceScope _scope;
    private readonly string? _previousSessionId;

    internal TestSessionScope(IServiceScope scope, string sessionId)
    {
        _scope = scope;
        _previousSessionId = TestSessionContext.CurrentSessionId;
        TestSessionContext.CurrentSessionId = sessionId;
    }

    /// <summary>
    /// The service provider for this scope.
    /// </summary>
    public IServiceProvider ServiceProvider => _scope.ServiceProvider;

    /// <inheritdoc />
    public void Dispose()
    {
        TestSessionContext.CurrentSessionId = _previousSessionId;
        _scope.Dispose();
    }
}
