using System.Reflection;
using System.Text.Json;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Assertion and query extension methods for <see cref="MessageSink"/>.
/// Provides a fluent API for verifying messages captured during tests.
/// </summary>
public static class MessageSinkAssertions
{
    /// <summary>
    /// Asserts that at least one message of the specified type was captured,
    /// optionally matching a predicate. Returns the first match as a typed <see cref="SentMessage{T}"/>.
    /// </summary>
    public static SentMessage<TMessage> ShouldContain<TMessage>(
        this MessageSink sink,
        Func<TMessage, bool>? predicate = null,
        JsonSerializerOptions? options = null)
    {
        var matching = sink.Messages
            .Where(m => MatchesType<TMessage>(m, sink.Registry))
            .ToList();

        if (matching.Count == 0)
        {
            throw new RatatoskrTestException(BuildNoMatchMessage<TMessage>(sink));
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

    /// <summary>
    /// Asserts that no message of the specified type was captured.
    /// </summary>
    public static void ShouldNotContain<TMessage>(this MessageSink sink)
    {
        var matching = sink.Messages.Where(m => MatchesType<TMessage>(m, sink.Registry)).ToList();

        if (matching.Count > 0)
        {
            throw new RatatoskrTestException(
                $"Expected no messages of type {typeof(TMessage).Name} to be sent, " +
                $"but found {matching.Count}.");
        }
    }

    /// <summary>
    /// Asserts that no messages were captured.
    /// </summary>
    public static void ShouldBeEmpty(this MessageSink sink)
    {
        if (sink.Count > 0)
        {
            throw new RatatoskrTestException(
                $"Expected no messages to be sent, but found {sink.Count}.");
        }
    }

    /// <summary>
    /// Asserts that exactly the specified number of messages were captured (all types).
    /// </summary>
    public static void ShouldHaveCount(this MessageSink sink, int expectedCount)
    {
        var actualCount = sink.Count;
        if (actualCount != expectedCount)
        {
            throw new RatatoskrTestException(
                $"Expected {expectedCount} message(s) to be sent, but found {actualCount}.");
        }
    }

    /// <summary>
    /// Asserts that exactly the specified number of messages of the given type were captured.
    /// </summary>
    public static void ShouldHaveCount<TMessage>(this MessageSink sink, int expectedCount)
    {
        var matching = sink.Messages.Where(m => MatchesType<TMessage>(m, sink.Registry)).ToList();

        if (matching.Count != expectedCount)
        {
            throw new RatatoskrTestException(
                $"Expected {expectedCount} message(s) of type {typeof(TMessage).Name} to be sent, " +
                $"but found {matching.Count}. " +
                BuildTypeMatchHint<TMessage>(sink));
        }
    }

    /// <summary>
    /// Gets all captured messages of the specified type as typed <see cref="SentMessage{T}"/> instances.
    /// </summary>
    public static IReadOnlyList<SentMessage<TMessage>> GetMessages<TMessage>(
        this MessageSink sink,
        JsonSerializerOptions? options = null)
    {
        return sink.Messages
            .Where(m => MatchesType<TMessage>(m, sink.Registry))
            .Select(m => new SentMessage<TMessage>(m.Deserialize<TMessage>(options)!, m.Properties, m.SentAt))
            .ToList();
    }

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

    private static bool MatchesType<TMessage>(SentMessage message, ChannelRegistry? registry)
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
        // We avoid sniffing the JSON content to prevent false positives and hidden bugs.
        return false;
    }

    private static string BuildNoMatchMessage<TMessage>(MessageSink sink)
    {
        var type = typeof(TMessage);
        var sentTypes = sink.Messages.Select(m => m.Properties.Type).ToList();

        var message = $"Expected to find a sent message of type {type.Name}, but none were found. " +
                      $"Messages sent: [{string.Join(", ", sentTypes)}]";

        // Check if type resolution would fail
        var hasAttribute = type.GetCustomAttribute<RatatoskrMessageAttribute>() != null;
        var hasRegistration = sink.Registry?.FindPublishChannelForMessage(type) != null;

        if (!hasAttribute && !hasRegistration)
        {
            message += $"\n\nHint: {type.Name} has no [RatatoskrMessage] attribute and is not registered " +
                       "in a publish channel (Produces<T>()). The assertion cannot match messages without " +
                       "a known type name. Add [RatatoskrMessage(\"your.type\")] to the class or register " +
                       "it in a publish channel.";
        }

        return message;
    }

    private static string BuildTypeMatchHint<TMessage>(MessageSink sink)
    {
        var type = typeof(TMessage);
        var hasAttribute = type.GetCustomAttribute<RatatoskrMessageAttribute>() != null;
        var hasRegistration = sink.Registry?.FindPublishChannelForMessage(type) != null;

        if (!hasAttribute && !hasRegistration)
        {
            return $"Hint: {type.Name} has no [RatatoskrMessage] attribute and no publish channel registration.";
        }

        return "";
    }
}
