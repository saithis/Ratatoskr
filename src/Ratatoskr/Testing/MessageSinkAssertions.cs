using System.Reflection;
using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Assertion and query extension methods for <see cref="MessageSink"/> and <see cref="MessageSinkView"/>.
/// Provides a fluent API for verifying messages captured during tests.
/// </summary>
public static class MessageSinkAssertions
{
    // --- MessageSink extensions ---

    /// <summary>
    /// Asserts that at least one message of the specified type was captured,
    /// optionally matching a predicate. Returns the first match as a typed <see cref="SentMessage{T}"/>.
    /// </summary>
    public static SentMessage<TMessage> ShouldContain<TMessage>(
        this MessageSink sink,
        Func<TMessage, bool>? predicate = null,
        JsonSerializerOptions? options = null)
        => ShouldContainCore<TMessage>(sink.Messages, sink.Registry, predicate, options);

    /// <summary>
    /// Asserts that no message of the specified type was captured.
    /// </summary>
    public static void ShouldNotContain<TMessage>(this MessageSink sink)
        => ShouldNotContainCore<TMessage>(sink.Messages, sink.Registry);

    /// <summary>
    /// Asserts that no messages were captured.
    /// </summary>
    public static void ShouldBeEmpty(this MessageSink sink)
        => ShouldBeEmptyCore(sink.Count);

    /// <summary>
    /// Asserts that exactly the specified number of messages were captured (all types).
    /// </summary>
    public static void ShouldHaveCount(this MessageSink sink, int expectedCount)
        => ShouldHaveCountCore(sink.Count, expectedCount);

    /// <summary>
    /// Asserts that exactly the specified number of messages of the given type were captured.
    /// </summary>
    public static void ShouldHaveCount<TMessage>(this MessageSink sink, int expectedCount)
        => ShouldHaveCountCore<TMessage>(sink.Messages, sink.Registry, expectedCount);

    /// <summary>
    /// Gets all captured messages of the specified type as typed <see cref="SentMessage{T}"/> instances.
    /// </summary>
    public static IReadOnlyList<SentMessage<TMessage>> GetMessages<TMessage>(
        this MessageSink sink,
        JsonSerializerOptions? options = null)
        => GetMessagesCore<TMessage>(sink.Messages, sink.Registry, options);

    /// <summary>
    /// Waits for a message of the specified type to be captured.
    /// Checks existing messages first to avoid race conditions.
    /// Returns a typed <see cref="SentMessage{T}"/>.
    /// </summary>
    public static async Task<SentMessage<TMessage>> WaitForAsync<TMessage>(
        this MessageSink sink,
        Func<TMessage, bool>? predicate = null,
        TimeSpan? timeout = null,
        JsonSerializerOptions? options = null)
    {
        var raw = await sink.WaitForAsync(m =>
        {
            if (!MatchesType<TMessage>(m, sink.Registry)) return false;
            if (predicate == null) return true;

            var item = m.Deserialize<TMessage>(options);
            return item != null && predicate(item);
        }, timeout);

        return new SentMessage<TMessage>(raw.Deserialize<TMessage>(options)!, raw.Properties, raw.SentAt);
    }

    // --- MessageSinkView extensions ---

    /// <summary>
    /// Asserts that at least one message of the specified type was captured in this session,
    /// optionally matching a predicate. Returns the first match as a typed <see cref="SentMessage{T}"/>.
    /// </summary>
    public static SentMessage<TMessage> ShouldContain<TMessage>(
        this MessageSinkView view,
        Func<TMessage, bool>? predicate = null,
        JsonSerializerOptions? options = null)
        => ShouldContainCore<TMessage>(view.Messages, view.Registry, predicate, options);

    /// <summary>
    /// Asserts that no message of the specified type was captured in this session.
    /// </summary>
    public static void ShouldNotContain<TMessage>(this MessageSinkView view)
        => ShouldNotContainCore<TMessage>(view.Messages, view.Registry);

    /// <summary>
    /// Asserts that no messages were captured in this session.
    /// </summary>
    public static void ShouldBeEmpty(this MessageSinkView view)
        => ShouldBeEmptyCore(view.Count);

    /// <summary>
    /// Asserts that exactly the specified number of messages were captured in this session.
    /// </summary>
    public static void ShouldHaveCount(this MessageSinkView view, int expectedCount)
        => ShouldHaveCountCore(view.Count, expectedCount);

    /// <summary>
    /// Asserts that exactly the specified number of messages of the given type were captured in this session.
    /// </summary>
    public static void ShouldHaveCount<TMessage>(this MessageSinkView view, int expectedCount)
        => ShouldHaveCountCore<TMessage>(view.Messages, view.Registry, expectedCount);

    /// <summary>
    /// Gets all captured messages of the specified type in this session.
    /// </summary>
    public static IReadOnlyList<SentMessage<TMessage>> GetMessages<TMessage>(
        this MessageSinkView view,
        JsonSerializerOptions? options = null)
        => GetMessagesCore<TMessage>(view.Messages, view.Registry, options);

    /// <summary>
    /// Waits for a message of the specified type to be captured in this session.
    /// Checks existing messages first to avoid race conditions.
    /// Returns a typed <see cref="SentMessage{T}"/>.
    /// </summary>
    public static async Task<SentMessage<TMessage>> WaitForAsync<TMessage>(
        this MessageSinkView view,
        Func<TMessage, bool>? predicate = null,
        TimeSpan? timeout = null,
        JsonSerializerOptions? options = null)
    {
        var raw = await view.WaitForAsync(m =>
        {
            if (!MatchesType<TMessage>(m, view.Registry)) return false;
            if (predicate == null) return true;

            var item = m.Deserialize<TMessage>(options);
            return item != null && predicate(item);
        }, timeout);

        return new SentMessage<TMessage>(raw.Deserialize<TMessage>(options)!, raw.Properties, raw.SentAt);
    }

    // --- Shared implementation ---

    internal static bool MatchesType<TMessage>(SentMessage message, ChannelRegistry? registry)
    {
        var type = typeof(TMessage);
        string? expectedTypeName = null;

        // 1. Check registry first if available
        var messageRegistration = registry?.FindPublishChannelForMessage(type)?.GetMessage(type);
        if (messageRegistration != null)
        {
            expectedTypeName = messageRegistration.MessageTypeName;
        }

        // 2. Fallback to CloudEvent attribute if not found in registry (or registry missing)
        if (expectedTypeName == null)
        {
            var messageAttribute = type.GetCustomAttribute<RatatoskrMessageAttribute>();
            expectedTypeName = messageAttribute?.Type;
        }

        if (expectedTypeName != null)
        {
            return message.Properties.Type?.Equals(expectedTypeName, StringComparison.OrdinalIgnoreCase) == true;
        }

        // If we can't determine the expected type, we can't match it reliably.
        return false;
    }

    private static SentMessage<TMessage> ShouldContainCore<TMessage>(
        IReadOnlyCollection<SentMessage> messages,
        ChannelRegistry? registry,
        Func<TMessage, bool>? predicate,
        JsonSerializerOptions? options)
    {
        var matching = messages
            .Where(m => MatchesType<TMessage>(m, registry))
            .ToList();

        if (matching.Count == 0)
        {
            throw new RatatoskrTestException(BuildNoMatchMessage<TMessage>(messages, registry));
        }

        if (predicate != null)
        {
            var withPredicate = matching
                .Select(m => new { Raw = m, Deserialized = m.Deserialize<TMessage>(options)! })
                .Where(x => predicate(x.Deserialized))
                .ToList();

            if (withPredicate.Count == 0)
            {
                throw new RatatoskrTestException(
                    $"Found {matching.Count} message(s) of type {typeof(TMessage).Name}, " +
                    "but none matched the predicate.");
            }

            var first = withPredicate.First();
            return new SentMessage<TMessage>(first.Deserialized, first.Raw.Properties, first.Raw.SentAt);
        }

        var match = matching.First();
        return new SentMessage<TMessage>(match.Deserialize<TMessage>(options)!, match.Properties, match.SentAt);
    }

    private static void ShouldNotContainCore<TMessage>(
        IReadOnlyCollection<SentMessage> messages,
        ChannelRegistry? registry)
    {
        var matching = messages.Where(m => MatchesType<TMessage>(m, registry)).ToList();

        if (matching.Count > 0)
        {
            throw new RatatoskrTestException(
                $"Expected no messages of type {typeof(TMessage).Name} to be sent, " +
                $"but found {matching.Count}.");
        }
    }

    private static void ShouldBeEmptyCore(int count)
    {
        if (count > 0)
        {
            throw new RatatoskrTestException(
                $"Expected no messages to be sent, but found {count}.");
        }
    }

    private static void ShouldHaveCountCore(int actualCount, int expectedCount)
    {
        if (actualCount != expectedCount)
        {
            throw new RatatoskrTestException(
                $"Expected {expectedCount} message(s) to be sent, but found {actualCount}.");
        }
    }

    private static void ShouldHaveCountCore<TMessage>(
        IReadOnlyCollection<SentMessage> messages,
        ChannelRegistry? registry,
        int expectedCount)
    {
        var matching = messages.Where(m => MatchesType<TMessage>(m, registry)).ToList();

        if (matching.Count != expectedCount)
        {
            throw new RatatoskrTestException(
                $"Expected {expectedCount} message(s) of type {typeof(TMessage).Name} to be sent, " +
                $"but found {matching.Count}. " +
                BuildTypeMatchHint<TMessage>(registry));
        }
    }

    private static IReadOnlyList<SentMessage<TMessage>> GetMessagesCore<TMessage>(
        IReadOnlyCollection<SentMessage> messages,
        ChannelRegistry? registry,
        JsonSerializerOptions? options)
    {
        return messages
            .Where(m => MatchesType<TMessage>(m, registry))
            .Select(m => new SentMessage<TMessage>(m.Deserialize<TMessage>(options)!, m.Properties, m.SentAt))
            .ToList();
    }

    private static string BuildNoMatchMessage<TMessage>(
        IReadOnlyCollection<SentMessage> messages,
        ChannelRegistry? registry)
    {
        var type = typeof(TMessage);
        var sentTypes = messages.Select(m => m.Properties.Type).ToList();

        var message = $"Expected to find a sent message of type {type.Name}, but none were found. " +
                      $"Messages sent: [{string.Join(", ", sentTypes)}]";

        var hasAttribute = type.GetCustomAttribute<RatatoskrMessageAttribute>() != null;
        var hasRegistration = registry?.FindPublishChannelForMessage(type) != null;

        if (!hasAttribute && !hasRegistration)
        {
            message += $"\n\nHint: {type.Name} has no [RatatoskrMessage] attribute and is not registered " +
                       "in a publish channel (Produces<T>()). The assertion cannot match messages without " +
                       "a known type name. Add [RatatoskrMessage(\"your.type\")] to the class or register " +
                       "it in a publish channel.";
        }

        return message;
    }

    private static string BuildTypeMatchHint<TMessage>(ChannelRegistry? registry)
    {
        var type = typeof(TMessage);
        var hasAttribute = type.GetCustomAttribute<RatatoskrMessageAttribute>() != null;
        var hasRegistration = registry?.FindPublishChannelForMessage(type) != null;

        if (!hasAttribute && !hasRegistration)
        {
            return $"Hint: {type.Name} has no [RatatoskrMessage] attribute and no publish channel registration.";
        }

        return "";
    }
}
