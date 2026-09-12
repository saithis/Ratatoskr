using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Ratatoskr.Management.Contracts;

/// <summary>
/// Transport-owned routing information learned from a service announcement — for RabbitMQ, the
/// command exchange plus the service and instance routing keys. Opaque to everything above the
/// transport, and never exposed by the dashboard API.
/// </summary>
/// <remarks>
/// Modelled as a string map rather than a transport-specific type so that the announcement stays
/// serializable by a package that knows nothing about any particular broker.
/// </remarks>
public sealed record ManagementAddress
{
    /// <summary>An address carrying nothing, used by transports that need no routing data.</summary>
    public static readonly ManagementAddress Empty = new();

    private readonly IReadOnlyDictionary<string, string> _values =
        FrozenDictionary<string, string>.Empty;

    /// <summary>The transport-defined routing values.</summary>
    public IReadOnlyDictionary<string, string> Values
    {
        get => _values;
        init =>
            _values =
                value is null or { Count: 0 }
                    ? FrozenDictionary<string, string>.Empty
                    : value.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Creates an address from transport-defined key/value pairs.</summary>
    public static ManagementAddress From(IReadOnlyDictionary<string, string> values) =>
        new() { Values = values };

    /// <summary>Returns the value for <paramref name="key"/>, or null when absent.</summary>
    public string? Get(string key) => Values.GetValueOrDefault(key);

    /// <summary>Whether this address carries no routing data.</summary>
    [JsonIgnore]
    public bool IsEmpty => Values.Count == 0;
}
