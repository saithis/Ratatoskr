namespace Ratatoskr.Management.Contracts;

/// <summary>
/// Versioning for the management control-plane protocol.
/// </summary>
public static class ManagementProtocol
{
    /// <summary>The protocol version this build speaks.</summary>
    public static readonly ProtocolVersion Current = new(1, 0);
}

/// <summary>
/// A major/minor protocol version. Minor versions are additive: a peer speaking a lower minor
/// version of the same major version is understood, a higher one is not.
/// </summary>
public sealed record ProtocolVersion(int Major, int Minor)
{
    /// <summary>
    /// Returns <see langword="true"/> when a peer speaking this version can be served by a peer
    /// that supports <paramref name="supported"/>.
    /// </summary>
    public bool IsCompatibleWith(ProtocolVersion supported)
    {
        ArgumentNullException.ThrowIfNull(supported);
        return Major == supported.Major && Minor <= supported.Minor;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}";
}
