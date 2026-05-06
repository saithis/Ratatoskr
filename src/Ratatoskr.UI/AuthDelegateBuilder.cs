namespace Ratatoskr.UI;

/// <summary>
/// Fluent builder for configuring how outgoing requests to a remote backend are authenticated.
/// </summary>
public sealed class AuthDelegateBuilder
{
    private Func<HttpRequestMessage, Task>? _delegate;

    /// <summary>
    /// Provides a custom delegate that mutates each outgoing <see cref="HttpRequestMessage"/>
    /// (e.g., attach a Bearer token, add an API-key header, etc.).
    /// </summary>
    public void UseDelegate(Func<HttpRequestMessage, Task> handler) =>
        _delegate = handler;

    internal Func<HttpRequestMessage, Task>? Build() => _delegate;
}
