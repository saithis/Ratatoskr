namespace Ratatoskr.Testing;

/// <summary>
/// Provides ambient test session context via AsyncLocal.
/// Used to propagate session IDs through async call chains so that
/// messages published during a test session can be tagged and filtered.
/// </summary>
public static class TestSessionContext
{
    internal const string SessionHeaderName = "x-ratatoskr-session";

    private static readonly AsyncLocal<string?> SessionIdValue = new();

    /// <summary>
    /// Gets or sets the current test session ID for the async execution context.
    /// </summary>
    public static string? CurrentSessionId
    {
        get => SessionIdValue.Value;
        set => SessionIdValue.Value = value;
    }
}
