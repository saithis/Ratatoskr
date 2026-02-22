using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Decorator for <see cref="IMessagePropertiesEnricher"/> that injects the current
/// test session ID (from <see cref="TestSessionContext"/>) into message headers.
/// This enables session-scoped message tracking for parallel test execution.
/// </summary>
internal class TestSessionEnricher(IMessagePropertiesEnricher inner) : IMessagePropertiesEnricher
{
    public MessageProperties Enrich<TMessage>(MessageProperties? properties) where TMessage : notnull
    {
        properties = inner.Enrich<TMessage>(properties);
        InjectSessionId(properties);
        return properties;
    }

    public MessageProperties Enrich(Type messageType, MessageProperties? properties)
    {
        properties = inner.Enrich(messageType, properties);
        InjectSessionId(properties);
        return properties;
    }

    private static void InjectSessionId(MessageProperties properties)
    {
        var sessionId = TestSessionContext.CurrentSessionId;
        if (sessionId != null)
        {
            properties.Headers[TestSessionContext.SessionHeaderName] = sessionId;
        }
    }
}
