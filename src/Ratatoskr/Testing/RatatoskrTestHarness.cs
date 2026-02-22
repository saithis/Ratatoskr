using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// The single entry point for testing Ratatoskr applications.
/// Allows creating isolated test sessions, simulating incoming messages,
/// asserting on sent messages, and integrating with the outbox pattern.
/// </summary>
public class RatatoskrTestHarness(
    MessageSink sent,
    MessageDispatcher dispatcher,
    IMessageSerializer serializer,
    IMessagePropertiesEnricher enricher,
    IServiceProvider serviceProvider)
{
    /// <summary>
    /// Gets the global message sink containing all captured sent messages.
    /// For parallel-safe testing, prefer <see cref="CreateSession"/> instead.
    /// </summary>
    public MessageSink Sent => sent;

    /// <summary>
    /// Gets the service provider associated with this harness.
    /// Used internally by extension methods (e.g., outbox processing).
    /// </summary>
    internal IServiceProvider ServiceProvider => serviceProvider;

    /// <summary>
    /// Creates an isolated test session for parallel-safe testing.
    /// Each session has a unique ID and only sees messages published within its context.
    /// </summary>
    /// <example>
    /// <code>
    /// await using var session = harness.CreateSession();
    /// await session.SimulateReceiveAsync(new OrderCreated { OrderId = "123" });
    /// session.Sent.ShouldContain&lt;OrderCreated&gt;();
    /// </code>
    /// </example>
    public TestSession CreateSession()
    {
        return new TestSession(sent, serviceProvider, dispatcher, serializer, enricher);
    }

    /// <summary>
    /// Simulates receiving a message of the specified type.
    /// This will be dispatched to the registered handlers for this message type.
    /// Throws <see cref="RatatoskrTestException"/> if no handlers are found or a permanent error occurs.
    /// </summary>
    /// <remarks>
    /// For parallel-safe testing, prefer <see cref="CreateSession"/> and use
    /// <see cref="TestSession.SimulateReceiveAsync{TMessage}"/> instead.
    /// </remarks>
    public async Task<DispatchResult> SimulateReceiveAsync<TMessage>(
        TMessage message,
        MessageProperties? properties = null,
        CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        // Enrich properties or create new ones
        properties = enricher.Enrich<TMessage>(properties);

        // Serialize the message
        var body = serializer.Serialize(message);
        properties.ContentType = serializer.ContentType;

        // Dispatch via the dispatcher
        var result = await dispatcher.DispatchAsync(body, properties, cancellationToken);

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

    /// <summary>
    /// Resets all test state. Clears all captured messages and cancels pending waiters.
    /// </summary>
    public void Reset() => sent.Clear();
}

/// <summary>
/// Exception thrown when a Ratatoskr test operation fails due to misconfiguration or unexpected state.
/// </summary>
public class RatatoskrTestException(string message) : Exception(message);
