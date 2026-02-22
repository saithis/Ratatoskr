using Microsoft.AspNetCore.TestHost;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// A test session that integrates with <see cref="TestServer"/> to create
/// session-aware HTTP clients. All messages published during HTTP requests
/// made through <see cref="CreateHttpClient"/> are tagged with this session's ID.
/// </summary>
/// <example>
/// <code>
/// await using var session = factory.CreateTestSession();
/// var client = session.CreateHttpClient();
///
/// await client.PostAsJsonAsync("/api/orders", new { ProductId = "abc" });
///
/// session.Sent.ShouldContain&lt;OrderCreated&gt;(m => m.ProductId == "abc");
/// </code>
/// </example>
public class WebTestSession : IAsyncDisposable
{
    private readonly TestSession _inner;
    private readonly TestServer _server;

    internal WebTestSession(TestSession inner, TestServer server)
    {
        _inner = inner;
        _server = server;
    }

    /// <summary>
    /// The unique identifier for this test session.
    /// </summary>
    public string SessionId => _inner.SessionId;

    /// <summary>
    /// A filtered view of sent messages belonging to this session.
    /// Use assertion extensions like <c>ShouldContain&lt;T&gt;()</c> to verify messages.
    /// </summary>
    public MessageSinkView Sent => _inner.Sent;

    /// <summary>
    /// Creates an HTTP client that automatically injects the session ID header
    /// into all requests. Messages published during these requests are tagged
    /// with this session and visible through <see cref="Sent"/>.
    /// </summary>
    public HttpClient CreateHttpClient()
    {
        var handler = new TestSessionDelegatingHandler(SessionId)
        {
            InnerHandler = _server.CreateHandler()
        };

        return new HttpClient(handler)
        {
            BaseAddress = _server.BaseAddress
        };
    }

    /// <inheritdoc cref="TestSession.SimulateReceiveAsync{TMessage}"/>
    public Task<DispatchResult> SimulateReceiveAsync<TMessage>(
        TMessage message,
        MessageProperties? properties = null,
        CancellationToken cancellationToken = default)
        where TMessage : notnull
        => _inner.SimulateReceiveAsync(message, properties, cancellationToken);

    /// <inheritdoc cref="TestSession.CreateScope"/>
    public TestSessionScope CreateScope() => _inner.CreateScope();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
