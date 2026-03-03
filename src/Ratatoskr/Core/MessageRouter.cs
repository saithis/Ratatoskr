namespace Ratatoskr.Core;

/// <summary>
/// Routes incoming messages through an optional <see cref="IMessageRouteInterceptor"/>
/// (e.g. inbox acceptance) and then to the <see cref="MessageDispatcher"/> for handler invocation.
/// <para>
/// Transport consumers call <see cref="RouteAsync"/> instead of interacting with
/// the interceptor and <see cref="MessageDispatcher"/> separately.
/// </para>
/// </summary>
public class MessageRouter(
    MessageDispatcher dispatcher,
    IMessageRouteInterceptor? interceptor = null)
{
    public async Task<DispatchResult> RouteAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        CancellationToken cancellationToken,
        string? channelName = null)
    {
        var handlersAccepted = false;
        if (interceptor != null)
        {
            var interceptResult = await interceptor.BeforeDispatchAsync(
                body, properties, transportName, cancellationToken);
            handlersAccepted = interceptResult.HandlersAccepted;
        }

        var result = await dispatcher.DispatchAsync(
            body, properties, cancellationToken, channelName, transportName);

        if (result == DispatchResult.NoHandlers && handlersAccepted)
            result = DispatchResult.Success;

        return result;
    }
}
