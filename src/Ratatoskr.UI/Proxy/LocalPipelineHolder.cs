using Microsoft.AspNetCore.Http;

namespace Ratatoskr.UI.Proxy;

/// <summary>
/// Holds the captured downstream ASP.NET Core request pipeline used by
/// <see cref="LocalBackendDispatcher"/> to dispatch requests in-process.
/// Populated by <c>UseRatatoskrUi</c> on the first request.
/// </summary>
internal sealed class LocalPipelineHolder
{
    private RequestDelegate? _pipeline;

    /// <summary>
    /// Sets the pipeline exactly once (thread-safe). Subsequent calls are ignored.
    /// </summary>
    internal void TrySet(RequestDelegate pipeline) =>
        Interlocked.CompareExchange(ref _pipeline, pipeline, null);

    internal RequestDelegate? Pipeline => _pipeline;
}
