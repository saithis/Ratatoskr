using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Ratatoskr.Endpoints;

namespace Ratatoskr.UI.Proxy;

/// <summary>
/// Dispatches management API requests in-process, bypassing the HTTP round-trip.
/// <para>
/// The caller's <see cref="System.Security.Claims.ClaimsPrincipal"/> is propagated to the
/// synthetic context so the management API's authorization policy still applies naturally —
/// no bypass is needed if the user already satisfies the management policy.
/// </para>
/// <para>
/// <see cref="ILocalRatatoskrRequestFeature"/> is set on the synthetic context as a trusted
/// in-process marker. It cannot be injected via an HTTP request because the feature type is
/// internal to the Ratatoskr assembly.
/// </para>
/// </summary>
internal sealed class LocalBackendDispatcher(
    LocalPipelineHolder pipelineHolder,
    IHttpContextFactory contextFactory)
{
    public async Task<HttpResponseMessage> DispatchAsync(
        HttpContext incomingContext,
        string targetPath,
        CancellationToken ct)
    {
        var pipeline = pipelineHolder.Pipeline
            ?? throw new InvalidOperationException(
                "The local backend pipeline has not been captured yet. " +
                "Ensure UseRatatoskrUi() is placed before UseAuthentication() and UseRouting() in the pipeline.");

        // Build a feature collection that inherits from the incoming context so we share
        // any infrastructure features (IServiceProvidersFeature, etc.) but override HTTP
        // request/response features for the synthetic request.
        var features = new FeatureCollection(incomingContext.Features);

        // Shadow routing features so the EndpointRoutingMiddleware re-routes based on the
        // synthetic path instead of inheriting the already-matched endpoint from the
        // incoming request. Without this, routing skips re-matching (endpoint != null check)
        // and executes the incoming endpoint again — causing infinite recursion.
        features.Set<IEndpointFeature>(new SyntheticEndpointFeature());
        features.Set<IRouteValuesFeature>(new SyntheticRouteValuesFeature());

        // Mark this as an in-process request. The feature type is internal, so it cannot be
        // attached from outside the Ratatoskr assembly — this is intentional.
        features.Set<ILocalRatatoskrRequestFeature>(new LocalRatatoskrRequestFeature());

        // IHttpContextFactory.Create (not CreateContext) is the correct method.
        var syntheticContext = contextFactory.Create(features);

        syntheticContext.Request.Method = incomingContext.Request.Method;
        syntheticContext.Request.Path = targetPath;
        syntheticContext.Request.QueryString = incomingContext.Request.QueryString;
        syntheticContext.Request.ContentType = incomingContext.Request.ContentType;

        if (incomingContext.Request.ContentLength > 0)
        {
            syntheticContext.Request.Body = incomingContext.Request.Body;
            syntheticContext.Request.ContentLength = incomingContext.Request.ContentLength;
        }

        // Propagate the caller's principal so the management policy evaluates the same user.
        // Standard JWT/Cookie auth handlers return NoResult() when credentials are absent from
        // the synthetic request, leaving this pre-set principal intact.
        syntheticContext.User = incomingContext.User;

        var responseBuffer = new MemoryStream();
        syntheticContext.Response.Body = responseBuffer;

        await pipeline(syntheticContext);

        responseBuffer.Seek(0, SeekOrigin.Begin);

        var response = new HttpResponseMessage((HttpStatusCode)syntheticContext.Response.StatusCode)
        {
            Content = new StreamContent(responseBuffer)
        };

        foreach (var header in syntheticContext.Response.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value))
                response.Content.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value);
        }

        return response;
    }

    // Stub implementations used to shadow the incoming context's routing features.
    private sealed class SyntheticEndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }

    private sealed class SyntheticRouteValuesFeature : IRouteValuesFeature
    {
        public RouteValueDictionary RouteValues { get; set; } = new();
    }
}
