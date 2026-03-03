using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Routes incoming messages to the correct <see cref="IInboxAcceptor"/> based on
/// which DbContext is configured for the message's consume channel.
/// Replaces the single-DbContext <c>InboxRouteInterceptor&lt;TDbContext&gt;</c>.
/// </summary>
internal class CompositeInboxRouteInterceptor : IMessageRouteInterceptor
{
    private readonly Dictionary<string, IInboxAcceptor> _acceptorsByChannel;

    public CompositeInboxRouteInterceptor(
        InboxChannelMap channelMap,
        IEnumerable<IInboxAcceptor> acceptors)
    {
        // Build a lookup from DbContext type to acceptor
        var acceptorsByType = new Dictionary<Type, IInboxAcceptor>();
        foreach (var acceptor in acceptors)
            acceptorsByType[acceptor.DbContextType] = acceptor;

        // Map each inbox-managed channel to its acceptor
        _acceptorsByChannel = new Dictionary<string, IInboxAcceptor>(StringComparer.Ordinal);
        foreach (var (channelName, dbContextType) in channelMap.GetAll())
        {
            if (acceptorsByType.TryGetValue(dbContextType, out var acceptor))
                _acceptorsByChannel[channelName] = acceptor;
        }
    }

    public async Task<RouteInterceptResult> BeforeDispatchAsync(
        byte[] body, MessageProperties properties, string transportName,
        string channelName, CancellationToken cancellationToken)
    {
        if (!_acceptorsByChannel.TryGetValue(channelName, out var acceptor))
            return new RouteInterceptResult(HandlersAccepted: false);

        var outcome = await acceptor.AcceptAsync(body, properties, transportName, channelName, cancellationToken);
        var accepted = outcome is InboxAcceptOutcome.Accepted or InboxAcceptOutcome.Duplicate;
        return new RouteInterceptResult(HandlersAccepted: accepted, SkipDispatch: accepted);
    }
}
