using Microsoft.EntityFrameworkCore;
using Ratatoskr.Core;
using Ratatoskr.EfCore;

namespace Ratatoskr;

/// <summary>
/// Interface for publishing messages in Ratatoskr.
/// Supports both transactional publishing via the EF Core outbox and direct broker publishing.
/// </summary>
public interface IRatatoskr
{
    /// <summary>
    /// Stages a message to be published transactionally via the outbox of the default DbContext.
    /// The message will be persisted to the database and sent when <c>SaveChangesAsync</c> is called on the DbContext.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PublishAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull;

    /// <summary>
    /// Stages a message to be published transactionally via the outbox of the default DbContext with configuration.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="configure">Configuration for publish options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PublishAsync<TMessage>(
        TMessage message,
        Action<PublishOptions> configure,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull;

    /// <summary>
    /// Stages a message to be published transactionally via the outbox of the default DbContext with explicit message properties.
    /// </summary>
    public Task PublishAsync<TMessage>(
        TMessage message,
        MessageProperties props,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull;

    /// <summary>
    /// Stages a message to be published transactionally via the outbox of the specified DbContext.
    /// Used when multiple DbContext types are configured.
    /// </summary>
    public Task PublishAsync<TDbContext, TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
        where TDbContext : DbContext, IRatatoskrDbContext
        where TMessage : notnull;

    /// <summary>
    /// Stages a message to be published transactionally via the outbox of the specified DbContext.
    /// Used when multiple DbContext types are configured.
    /// </summary>
    public Task PublishAsync<TDbContext, TMessage>(
        TMessage message,
        Action<PublishOptions> configure,
        CancellationToken cancellationToken = default
    )
        where TDbContext : DbContext, IRatatoskrDbContext
        where TMessage : notnull;

    /// <summary>
    /// Stages a message to be published transactionally via the outbox of the specified DbContext with explicit message properties.
    /// </summary>
    public Task PublishAsync<TDbContext, TMessage>(
        TMessage message,
        MessageProperties props,
        CancellationToken cancellationToken = default
    )
        where TDbContext : DbContext, IRatatoskrDbContext
        where TMessage : notnull;

    /// <summary>
    /// Publishes a message directly to the broker without outbox/transactional guarantees.
    /// Supports configuration of publish options including deferred delivery timestamps.
    /// </summary>
    public Task PublishDirectAsync<TMessage>(
        TMessage message,
        Action<PublishOptions>? configure,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull;

    /// <summary>
    /// Publishes a message directly without transactional guarantees.
    /// </summary>
    public Task PublishDirectAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken
    )
        where TMessage : notnull;

    /// <summary>
    /// Publishes a message immediately without transactional guarantees.
    /// The message is sent directly to the message broker.
    /// </summary>
    public Task PublishDirectAsync<TMessage>(
        TMessage message,
        MessageProperties? props = null,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull;
}
