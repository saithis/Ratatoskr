using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Provides a simple way to invoke message handlers in isolation for unit testing.
/// No DI container or bus configuration required.
/// </summary>
/// <example>
/// <code>
/// var handler = new OrderCreatedHandler(mockRepository.Object);
/// await HandlerTestInvoker.InvokeAsync(handler, new OrderCreated { OrderId = "123" });
/// mockRepository.Verify(r => r.SaveOrder(It.IsAny&lt;Order&gt;()), Times.Once);
/// </code>
/// </example>
public static class HandlerTestInvoker
{
    /// <summary>
    /// Invokes a message handler with the given message and optional properties.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="handler">The handler instance to invoke.</param>
    /// <param name="message">The message to dispatch.</param>
    /// <param name="properties">Optional message properties. If null, a default instance is created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task InvokeAsync<TMessage>(
        IMessageHandler<TMessage> handler,
        TMessage message,
        MessageProperties? properties = null,
        CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        return handler.HandleAsync(message, properties ?? new MessageProperties(), cancellationToken);
    }
}
