using Ratatoskr.Testing;

namespace Ratatoskr.EfCore.Testing;

/// <summary>
/// Assertion extensions for verifying outbox staged messages before SaveChanges.
/// </summary>
public static class OutboxStagingAssertions
{
    /// <summary>
    /// Asserts that a message of the specified type was staged in the outbox.
    /// </summary>
    public static void ShouldHaveStaged<TMessage>(
        this OutboxStagingCollection collection,
        Func<TMessage, bool>? predicate = null)
    {
        var matching = collection.Queue
            .Where(item => item.Message is TMessage)
            .ToList();

        if (matching.Count == 0)
        {
            var stagedTypes = collection.Queue
                .Select(item => item.Message.GetType().Name)
                .ToList();

            throw new RatatoskrTestException(
                $"Expected to find a staged message of type {typeof(TMessage).Name}, but none were found. " +
                $"Staged messages: [{string.Join(", ", stagedTypes)}]");
        }

        if (predicate != null)
        {
            var withPredicate = matching
                .Where(item => predicate((TMessage)item.Message))
                .ToList();

            if (withPredicate.Count == 0)
            {
                throw new RatatoskrTestException(
                    $"Found {matching.Count} staged message(s) of type {typeof(TMessage).Name}, " +
                    "but none matched the predicate.");
            }
        }
    }

    /// <summary>
    /// Asserts that no message of the specified type was staged in the outbox.
    /// </summary>
    public static void ShouldNotHaveStaged<TMessage>(this OutboxStagingCollection collection)
    {
        var matching = collection.Queue
            .Where(item => item.Message is TMessage)
            .ToList();

        if (matching.Count > 0)
        {
            throw new RatatoskrTestException(
                $"Expected no staged messages of type {typeof(TMessage).Name}, " +
                $"but found {matching.Count}.");
        }
    }

    /// <summary>
    /// Asserts that exactly the specified number of messages are staged.
    /// </summary>
    public static void ShouldHaveStagedCount(this OutboxStagingCollection collection, int expectedCount)
    {
        if (collection.Count != expectedCount)
        {
            throw new RatatoskrTestException(
                $"Expected {expectedCount} staged message(s), but found {collection.Count}.");
        }
    }

    /// <summary>
    /// Asserts that no messages are staged.
    /// </summary>
    public static void ShouldNotHaveStagedAny(this OutboxStagingCollection collection)
    {
        collection.ShouldHaveStagedCount(0);
    }
}
