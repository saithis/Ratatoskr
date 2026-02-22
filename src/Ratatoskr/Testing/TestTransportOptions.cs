namespace Ratatoskr.Testing;

/// <summary>
/// Options for configuring the Ratatoskr test transport.
/// </summary>
public class TestTransportOptions
{
    /// <summary>
    /// When <c>true</c> (default), replaces the real message transport with an in-memory implementation.
    /// When <c>false</c>, keeps the real transport but wraps it to capture sent messages for assertions.
    /// Set to <c>false</c> when using TestContainers with a real message broker.
    /// </summary>
    public bool ReplaceTransport { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, messages sent via <see cref="IRatatoskr"/> are also dispatched
    /// to registered handlers in-process. This enables true end-to-end testing without a real broker.
    /// When <c>false</c> (default), messages are only captured for assertions.
    /// </summary>
    public bool RouteMessages { get; set; }
}
