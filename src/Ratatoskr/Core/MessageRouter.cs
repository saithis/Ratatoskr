namespace Ratatoskr.Core;

/// <summary>
/// Routes incoming messages through registered <see cref="IMessageRouteInterceptor"/> instances
/// (e.g. inbox acceptance per DbContext) and then to the <see cref="MessageDispatcher"/> for handler invocation.
/// <para>
/// Transport consumers call <see cref="RouteAsync"/> instead of interacting with
/// interceptors and <see cref="MessageDispatcher"/> separately.
/// </para>
/// </summary>
public sealed class MessageRouter(
    MessageDispatcher dispatcher,
    IEnumerable<IMessageRouteInterceptor> interceptors
)
{
    private readonly IMessageRouteInterceptor[] _interceptors = [.. interceptors];

    public async Task<DispatchResult> RouteAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        CancellationToken cancellationToken,
        string channelName
    )
    {
        var handlersAccepted = false;
        foreach (var interceptor in _interceptors)
        {
            var interceptResult = await interceptor
                .BeforeDispatchAsync(
                    body,
                    properties,
                    transportName,
                    channelName,
                    cancellationToken
                )
                .ConfigureAwait(false);
            handlersAccepted |= interceptResult.HandlersAccepted;
        }

        var result = await dispatcher
            .DispatchAsync(body, properties, cancellationToken, channelName, transportName)
            .ConfigureAwait(false);

        if (result == DispatchResult.NoHandlers && handlersAccepted)
        {
            result = DispatchResult.Success;
        }

        return result;
    }
}
