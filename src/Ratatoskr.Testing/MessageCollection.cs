using System.Collections;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// A queryable collection of tracked messages with assertion helpers.
/// </summary>
public class MessageCollection : IEnumerable<TrackedMessage>
{
    private readonly Func<IEnumerable<TrackedMessage>> _source;

    internal MessageCollection(Func<IEnumerable<TrackedMessage>> source)
    {
        _source = source;
    }

    /// <summary>
    /// Gets the number of tracked messages in this collection.
    /// </summary>
    public int Count => _source().Count();

    /// <summary>
    /// Gets a single message of the specified type. Throws if zero or more than one match.
    /// </summary>
    public TrackedMessage Single<T>() where T : notnull
    {
        var typeName = GetTypeName<T>();
        var matches = _source().Where(m => MatchesType<T>(m)).ToList();

        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"Expected exactly one message of type '{typeof(T).Name}' (wire type: '{typeName}'), but found none."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Expected exactly one message of type '{typeof(T).Name}', but found {matches.Count}.")
        };
    }

    /// <summary>
    /// Gets the first message of the specified type. Throws if none found.
    /// </summary>
    public TrackedMessage First<T>() where T : notnull
    {
        var typeName = GetTypeName<T>();
        return _source().FirstOrDefault(m => MatchesType<T>(m))
            ?? throw new InvalidOperationException(
                $"Expected at least one message of type '{typeof(T).Name}' (wire type: '{typeName}'), but found none.");
    }

    /// <summary>
    /// Gets all messages of the specified type.
    /// </summary>
    public IReadOnlyList<TrackedMessage> All<T>() where T : notnull
    {
        return _source().Where(m => MatchesType<T>(m)).ToList();
    }

    /// <summary>
    /// Asserts that at least one message of the specified type exists. Returns the first match.
    /// </summary>
    public TrackedMessage ShouldHaveMessage<T>() where T : notnull
    {
        var typeName = GetTypeName<T>();
        return _source().FirstOrDefault(m => MatchesType<T>(m))
            ?? throw new InvalidOperationException(
                $"Expected at least one message of type '{typeof(T).Name}' (wire type: '{typeName}'), but found none. " +
                $"Messages in collection: [{string.Join(", ", _source().Select(m => m.Properties.Type ?? m.MessageType?.Name ?? "unknown"))}]");
    }

    /// <summary>
    /// Asserts that no messages of the specified type exist.
    /// </summary>
    public void ShouldHaveNoMessage<T>() where T : notnull
    {
        var match = _source().FirstOrDefault(m => MatchesType<T>(m));
        if (match != null)
        {
            throw new InvalidOperationException(
                $"Expected no messages of type '{typeof(T).Name}', but found {_source().Count(m => MatchesType<T>(m))}.");
        }
    }

    public IEnumerator<TrackedMessage> GetEnumerator() => _source().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool MatchesType<T>(TrackedMessage m)
    {
        // Match by CLR type first (most reliable)
        if (m.MessageType == typeof(T))
            return true;

        // Match by deserialized message instance type
        if (m.Activity.Message is T)
            return true;

        // Match by wire type name from attribute
        var typeName = GetTypeName<T>();
        if (typeName != null && m.Properties.Type == typeName)
            return true;

        return false;
    }

    private static string? GetTypeName<T>()
    {
        var attr = typeof(T).GetCustomAttributes(typeof(RatatoskrMessageAttribute), false)
            .FirstOrDefault() as RatatoskrMessageAttribute;
        return attr?.Type;
    }
}
