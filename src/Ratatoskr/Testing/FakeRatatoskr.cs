using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Represents a message that was published via <see cref="FakeRatatoskr"/>.
/// Stores the typed message directly without serialization.
/// </summary>
public record PublishedMessage(object Message, Type MessageType, MessageProperties Properties)
{
    /// <summary>
    /// Casts the message to the specified type.
    /// </summary>
    public T As<T>() => (T)Message;
}

/// <summary>
/// A simple test double for <see cref="IRatatoskr"/> that captures published messages
/// without serialization or any infrastructure dependencies.
/// Ideal for unit testing services that depend on <see cref="IRatatoskr"/>.
/// </summary>
/// <example>
/// <code>
/// var ratatoskr = new FakeRatatoskr();
/// var sut = new OrderService(ratatoskr);
///
/// await sut.PlaceOrderAsync(new PlaceOrderCommand { ProductId = "abc" });
///
/// ratatoskr.PublishedMessages.Should().ContainSingle();
/// var msg = ratatoskr.ShouldHavePublished&lt;OrderCreated&gt;(m => m.ProductId == "abc");
/// </code>
/// </example>
public class FakeRatatoskr : IRatatoskr
{
    private readonly List<PublishedMessage> _messages = new();
    private readonly object _lock = new();

    /// <summary>
    /// All messages that have been published.
    /// </summary>
    public IReadOnlyList<PublishedMessage> PublishedMessages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToList();
            }
        }
    }

    /// <inheritdoc />
    public Task PublishDirectAsync<TMessage>(
        TMessage message,
        MessageProperties? props = null,
        CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        lock (_lock)
        {
            _messages.Add(new PublishedMessage(message, typeof(TMessage), props ?? new MessageProperties()));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all captured messages.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
        }
    }
}
