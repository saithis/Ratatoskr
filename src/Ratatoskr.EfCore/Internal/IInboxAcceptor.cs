using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Non-generic interface for inbox acceptance, enabling runtime dispatch
/// to the correct <see cref="InboxAcceptor{TDbContext}"/> based on channel configuration.
/// </summary>
internal interface IInboxAcceptor
{
    /// <summary>The DbContext type this acceptor uses.</summary>
    Type DbContextType { get; }

    Task<InboxAcceptOutcome> AcceptAsync(
        byte[] body,
        MessageProperties properties,
        string transportName,
        string channelName,
        CancellationToken cancellationToken);
}
