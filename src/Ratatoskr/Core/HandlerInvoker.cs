using Microsoft.Extensions.DependencyInjection;

namespace Ratatoskr.Core;

/// <summary>
/// Invokes a single message handler in its own DI scope.
/// Shared by <see cref="MessageDispatcher"/> (for non-inbox handlers) and the inbox
/// processor (for inbox-managed handlers with per-handler retry and timeout).
/// </summary>
public sealed class HandlerInvoker(IServiceScopeFactory scopeFactory)
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
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(properties);

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var handler = scope.ServiceProvider.GetRequiredService(handlerType);
            var invoke = HandlerInvokerCache.Get(message.GetType());

            if (timeout.HasValue)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                timeoutCts.CancelAfter(timeout.Value);
                await invoke(handler, message, properties, timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                await invoke(handler, message, properties, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
