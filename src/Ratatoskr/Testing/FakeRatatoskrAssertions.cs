namespace Ratatoskr.Testing;

/// <summary>
/// Assertion extension methods for <see cref="FakeRatatoskr"/>.
/// Provides a fluent API for verifying published messages in unit tests.
/// </summary>
public static class FakeRatatoskrAssertions
{
    /// <summary>
    /// Asserts that at least one message of the specified type was published,
    /// optionally matching a predicate. Returns the first matching message.
    /// </summary>
    public static TMessage ShouldHavePublished<TMessage>(
        this FakeRatatoskr fake,
        Func<TMessage, bool>? predicate = null)
    {
        var matching = fake.PublishedMessages
            .Where(m => m.MessageType == typeof(TMessage))
            .ToList();

        if (matching.Count == 0)
        {
            var publishedTypes = fake.PublishedMessages
                .Select(m => m.MessageType.Name)
                .ToList();

            throw new RatatoskrTestException(
                $"Expected a published message of type {typeof(TMessage).Name}, but none were found. " +
                $"Published messages: [{string.Join(", ", publishedTypes)}]");
        }

        if (predicate != null)
        {
            var withPredicate = matching
                .Where(m => predicate(m.As<TMessage>()))
                .ToList();

            if (withPredicate.Count == 0)
            {
                throw new RatatoskrTestException(
                    $"Found {matching.Count} published message(s) of type {typeof(TMessage).Name}, " +
                    "but none matched the predicate.");
            }

            return withPredicate.First().As<TMessage>();
        }

        return matching.First().As<TMessage>();
    }

    /// <summary>
    /// Asserts that no message of the specified type was published.
    /// </summary>
    public static void ShouldNotHavePublished<TMessage>(this FakeRatatoskr fake)
    {
        var matching = fake.PublishedMessages
            .Where(m => m.MessageType == typeof(TMessage))
            .ToList();

        if (matching.Count > 0)
        {
            throw new RatatoskrTestException(
                $"Expected no published messages of type {typeof(TMessage).Name}, " +
                $"but found {matching.Count}.");
        }
    }

    /// <summary>
    /// Asserts that exactly the specified number of messages were published (all types).
    /// </summary>
    public static void ShouldHavePublishedCount(this FakeRatatoskr fake, int expectedCount)
    {
        var actualCount = fake.PublishedMessages.Count;
        if (actualCount != expectedCount)
        {
            throw new RatatoskrTestException(
                $"Expected {expectedCount} published message(s), but found {actualCount}.");
        }
    }

    /// <summary>
    /// Asserts that exactly the specified number of messages of the given type were published.
    /// </summary>
    public static void ShouldHavePublishedCount<TMessage>(this FakeRatatoskr fake, int expectedCount)
    {
        var matching = fake.PublishedMessages
            .Where(m => m.MessageType == typeof(TMessage))
            .ToList();

        if (matching.Count != expectedCount)
        {
            throw new RatatoskrTestException(
                $"Expected {expectedCount} published message(s) of type {typeof(TMessage).Name}, " +
                $"but found {matching.Count}.");
        }
    }

    /// <summary>
    /// Asserts that no messages were published.
    /// </summary>
    public static void ShouldBeEmpty(this FakeRatatoskr fake)
    {
        if (fake.PublishedMessages.Count > 0)
        {
            var publishedTypes = fake.PublishedMessages
                .Select(m => m.MessageType.Name)
                .ToList();

            throw new RatatoskrTestException(
                $"Expected no published messages, but found {fake.PublishedMessages.Count}: " +
                $"[{string.Join(", ", publishedTypes)}]");
        }
    }
}
