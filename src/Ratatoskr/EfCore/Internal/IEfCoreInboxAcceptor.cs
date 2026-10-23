using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Non-generic interface for <see cref="InboxAcceptor{TDbContext}"/> so that
/// <see cref="EfCoreMessageSender"/> can dispatch to the correct typed acceptor.
/// </summary>
internal interface IEfCoreInboxAcceptor
{
    public Type DbContextType { get; }

    public Task<InboxAcceptOutcome> AcceptAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        string channelName,
        CancellationToken cancellationToken
    );
}
