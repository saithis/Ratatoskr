namespace Ratatoskr.Testing;

/// <summary>
/// Options for configuring the Ratatoskr test infrastructure
/// when used with <see cref="WebApplicationFactoryExtensions.WithRatatoskrTestServices{TEntryPoint}"/>.
/// </summary>
public class RatatoskrTestOptions
{
    /// <summary>
    /// When <c>true</c> (default), replaces the real message transport with an in-memory implementation.
    /// When <c>false</c>, keeps the real transport but wraps it to capture sent messages for assertions.
    /// Set to <c>false</c> when using TestContainers with a real message broker.
    /// </summary>
    public bool ReplaceTransport { get; set; } = true;
}
