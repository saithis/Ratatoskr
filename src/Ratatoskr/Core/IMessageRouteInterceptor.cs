namespace Ratatoskr.Core;

/// <summary>
/// Intercepts message routing before dispatch.
/// Called by <see cref="MessageRouter"/> to allow infrastructure packages (e.g. inbox)
/// to accept or persist messages before the <see cref="MessageDispatcher"/> invokes handlers.
/// </summary>
public interface IMessageRouteInterceptor
{
    /// <summary>
    /// Called before a message is dispatched to handlers.
    /// </summary>
    /// <returns>Result indicating whether any handlers were accepted for deferred processing.</returns>
    Task<RouteInterceptResult> BeforeDispatchAsync(
        byte[] body, MessageProperties properties, string transportName,
        string channelName, CancellationToken cancellationToken);
}

/// <summary>
/// Result of <see cref="IMessageRouteInterceptor.BeforeDispatchAsync"/>.
/// </summary>
/// <param name="HandlersAccepted">
/// True if one or more handlers were accepted for deferred processing.
/// When true and the dispatcher returns <see cref="DispatchResult.NoHandlers"/>,
/// the <see cref="MessageRouter"/> treats the overall result as <see cref="DispatchResult.Success"/>.
/// </param>
/// <param name="SkipDispatch">
/// When true, the <see cref="MessageRouter"/> skips calling <see cref="MessageDispatcher"/>
/// entirely. Used when the interceptor fully handles the message (e.g. inbox-managed messages
/// where all handlers are deferred).
/// </param>
public record RouteInterceptResult(bool HandlersAccepted, bool SkipDispatch = false);
