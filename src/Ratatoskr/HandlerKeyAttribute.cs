namespace Ratatoskr;

/// <summary>
/// Assigns a stable key to a message handler, used as the deduplication and retry key by the inbox.
/// If a key is also provided via <c>AddHandler</c>, the <c>AddHandler</c> key takes precedence.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class HandlerKeyAttribute : Attribute
{
    /// <summary>
    /// The stable handler key (e.g., "fulfillment").
    /// </summary>
    public string Key { get; }

    public HandlerKeyAttribute(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be null or whitespace", nameof(key));
        Key = key;
    }
}
