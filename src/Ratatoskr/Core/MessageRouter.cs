namespace Ratatoskr.Core;

/// <summary>
/// Routes incoming messages through the inbox (if configured) and then to the
/// <see cref="MessageDispatcher"/> for non-inbox handler invocation.
/// <para>
/// Transport consumers call <see cref="RouteAsync"/> instead of interacting with
/// <see cref="IInboxAcceptor"/> and <see cref="MessageDispatcher"/> separately.
/// </para>
/// </summary>
public class MessageRouter(
    MessageDispatcher dispatcher,
    IInboxAcceptor? inboxAcceptor = null)
{
    public async Task<DispatchResult> RouteAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        CancellationToken cancellationToken,
        string? channelName = null)
    {
        var inboxAccepted = false;
        if (inboxAcceptor != null)
            inboxAccepted = await inboxAcceptor.AcceptAsync(
                body, properties, transportName, cancellationToken);

        var result = await dispatcher.DispatchAsync(
            body, properties, cancellationToken, channelName, transportName);

        if (result == DispatchResult.NoHandlers && inboxAccepted)
            result = DispatchResult.Success;

        return result;
    }
}
