using System.Collections;

namespace Ratatoskr.Testing;

/// <summary>
/// A queryable collection of tracked messages with assertion helpers.
/// </summary>
public class MessageCollection : IEnumerable<TrackedMessage>
{
    private readonly Func<IEnumerable<TrackedMessage>> _source;

    internal MessageCollection(Func<IEnumerable<TrackedMessage>> source) => _source = source;

    /// <summary>
    /// Gets the number of tracked messages in this collection.
    /// </summary>
    public int Count => _source().Count();

    /// <summary>
    /// Gets a single message of the specified type. Throws if zero or more than one match.
    /// </summary>
#pragma warning disable CA1720 // method name mirrors LINQ convention, not the float type
    public TrackedMessage Single<T>()
#pragma warning restore CA1720
        where T : notnull
    {
        var typeName = MessageTypeMatcher.GetTypeName(typeof(T));
        var matches = _source().Where(MessageTypeMatcher.Matches<T>).ToList();

        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"Expected exactly one message of type '{typeof(T).Name}' (wire type: '{typeName}'), but found none."
            ),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Expected exactly one message of type '{typeof(T).Name}', but found {matches.Count}."
            ),
        };
    }

    /// <summary>
    /// Gets the first message of the specified type. Throws if none found.
    /// </summary>
    public TrackedMessage First<T>()
        where T : notnull
    {
        var typeName = MessageTypeMatcher.GetTypeName(typeof(T));
        return _source().FirstOrDefault(MessageTypeMatcher.Matches<T>)
            ?? throw new InvalidOperationException(
                $"Expected at least one message of type '{typeof(T).Name}' (wire type: '{typeName}'), but found none."
            );
    }

    /// <summary>
    /// Gets all messages of the specified type.
    /// </summary>
    public IReadOnlyList<TrackedMessage> All<T>()
        where T : notnull
    {
        return _source().Where(MessageTypeMatcher.Matches<T>).ToList();
    }

    /// <summary>
    /// Asserts that at least one message of the specified type exists. Returns the first match.
    /// </summary>
    public TrackedMessage ShouldHaveMessage<T>()
        where T : notnull
    {
        var typeName = MessageTypeMatcher.GetTypeName(typeof(T));
        var messages = _source().ToList();
        return messages.FirstOrDefault(MessageTypeMatcher.Matches<T>)
            ?? throw new InvalidOperationException(
                $"Expected at least one message of type '{typeof(T).Name}' (wire type: '{typeName}'), but found none. "
                    + $"Messages in collection: [{string.Join(", ", messages.Select(m => m.Properties.Type ?? m.MessageType?.Name ?? "unknown"))}]"
            );
    }

    /// <summary>
    /// Asserts that no messages of the specified type exist.
    /// </summary>
    public void ShouldHaveNoMessage<T>()
        where T : notnull
    {
        var messages = _source().ToList();
        var count = messages.Count(MessageTypeMatcher.Matches<T>);
        if (count > 0)
        {
            throw new InvalidOperationException(
                $"Expected no messages of type '{typeof(T).Name}', but found {count.ToString(System.Globalization.CultureInfo.InvariantCulture)}."
            );
        }
    }

    public IEnumerator<TrackedMessage> GetEnumerator() => _source().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
