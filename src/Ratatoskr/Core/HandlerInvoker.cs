using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.Core;

/// <summary>
/// Invokes a single message handler in its own DI scope.
/// Shared by <see cref="MessageDispatcher"/> (for non-inbox handlers) and the inbox
/// processor (for inbox-managed handlers with per-handler retry and timeout).
/// </summary>
public class HandlerInvoker(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Resolves a handler by type in a fresh DI scope and invokes it via a compiled delegate.
    /// </summary>
    public async Task InvokeAsync(
        Type handlerType,
        object message,
        MessageProperties properties,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null
    )
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService(handlerType);
        var invoke = HandlerInvokerCache.Get(message.GetType());

        if (timeout.HasValue)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeoutCts.CancelAfter(timeout.Value);
            await invoke(handler, message, properties, timeoutCts.Token);
        }
        else
        {
            await invoke(handler, message, properties, cancellationToken);
        }
    }
}
