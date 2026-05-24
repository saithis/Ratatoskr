namespace Ratatoskr;

/// <summary>
/// Marks a class as a Ratatoskr message with the specified type identifier.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RatatoskrMessageAttribute : Attribute
{
    /// <summary>
    /// The CloudEvent type identifier (e.g., "com.example.order.created").
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Optional URI identifying the schema that the event data adheres to (CloudEvents <c>dataschema</c> attribute).
    /// </summary>
    public string? DataSchema { get; set; }

    public RatatoskrMessageAttribute(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("Type cannot be null or whitespace", nameof(type));
        }
        Type = type;
    }
}
