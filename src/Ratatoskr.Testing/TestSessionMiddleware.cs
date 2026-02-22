using Microsoft.AspNetCore.Http;
using Ratatoskr.Testing;

namespace Ratatoskr.Testing;

/// <summary>
/// ASP.NET Core middleware that reads the session ID from the
/// <c>X-Ratatoskr-Session</c> HTTP header and sets <see cref="TestSessionContext.CurrentSessionId"/>.
/// This enables messages published during an HTTP request to be tagged with the test session,
/// allowing parallel-safe test assertions.
/// </summary>
internal class TestSessionMiddleware(RequestDelegate next)
{
    internal const string HttpSessionHeaderName = "X-Ratatoskr-Session";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HttpSessionHeaderName, out var sessionId)
            && !string.IsNullOrEmpty(sessionId.ToString()))
        {
            var previousSessionId = TestSessionContext.CurrentSessionId;
            TestSessionContext.CurrentSessionId = sessionId.ToString();
            try
            {
                await next(context);
            }
            finally
            {
                TestSessionContext.CurrentSessionId = previousSessionId;
            }
        }
        else
        {
            await next(context);
        }
    }
}
