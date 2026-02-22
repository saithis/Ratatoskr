namespace Ratatoskr.Testing;

/// <summary>
/// HTTP message handler that injects the test session ID into outgoing requests.
/// Used by <see cref="WebTestSession.CreateHttpClient"/> to propagate the session
/// through the test server boundary.
/// </summary>
internal class TestSessionDelegatingHandler(string sessionId) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(TestSessionMiddleware.HttpSessionHeaderName, sessionId);
        return base.SendAsync(request, cancellationToken);
    }
}
