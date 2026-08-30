using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Ratatoskr.UI;

/// <summary>
/// Extension methods for mapping the Ratatoskr UI Dashboard in ASP.NET Core applications.
/// </summary>
public static class RatatoskrUIEndpointRouteBuilderExtensions
{
    private static readonly EmbeddedFileProvider FileProvider = new(
        typeof(RatatoskrUIEndpointRouteBuilderExtensions).Assembly,
        "Ratatoskr.UI.wwwroot"
    );

    /// <summary>
    /// Placeholder inside <c>index.html</c> that is replaced at request time with the absolute
    /// path the UI is served from. Relative asset URLs cannot be used because the browser
    /// resolves them against the current document path, which differs between
    /// <c>/ratatoskr</c> and <c>/ratatoskr/</c>.
    /// </summary>
    private const string BasePathToken = "__RATATOSKR_BASE__";

    /// <summary>Name of the <see cref="HttpClient"/> the multi-service relay proxy uses.</summary>
    internal const string ProxyHttpClientName = "RatatoskrUIProxy";

    /// <summary>
    /// Maps the Ratatoskr Management Dashboard web UI under <paramref name="routePrefix"/>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <param name="routePrefix">
    /// Path to serve the dashboard from. Defaults to <see cref="RatatoskrUIOptions.RoutePrefix"/>.
    /// </param>
    /// <param name="policyName">
    /// Optional authorization policy applied to the dashboard and its endpoints. Validated at
    /// startup.
    /// </param>
    public static IEndpointRouteBuilder MapRatatoskrUI(
        this IEndpointRouteBuilder endpoints,
        string? routePrefix = null,
        string? policyName = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options =
            endpoints.ServiceProvider.GetService<RatatoskrUIOptions>() ?? new RatatoskrUIOptions();
        var prefix = routePrefix ?? options.RoutePrefix;
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix, nameof(routePrefix));
        var normalizedPrefix = prefix.TrimEnd('/');

        ValidateAuthorizationPolicy(endpoints, policyName);
        ValidateProxyPrerequisites(endpoints, options);

        var group = endpoints.MapGroup(normalizedPrefix);
        if (!string.IsNullOrWhiteSpace(policyName))
        {
            group.RequireAuthorization(policyName);
        }
        group.DisableAntiforgery();

        // UI Config Endpoint
        group.MapGet(
            "/ui-api/config",
            (HttpContext httpContext) =>
            {
                // Everything the browser sees has to be rooted at PathBase, otherwise the UI
                // breaks as soon as the host is mounted behind a reverse proxy sub-path.
                var pathBase = httpContext.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
                return TypedResults.Ok(
                    new
                    {
                        title = options.Title,
                        routePrefix = pathBase + normalizedPrefix,
                        pollingIntervalMs = options.PollingIntervalMs,
                        enablePayloadEditing = options.EnablePayloadEditing,
                        includeLocalService = options.IncludeLocalService,
                        localServiceName = options.LocalServiceName,
                        defaultBasePath = pathBase + ManagementApiPathForLocalHost(options),
                        remoteServices = options.RemoteServices,
                    }
                );
            }
        );

        // UI Multi-Service Relay Proxy Endpoint. Only mapped when there is something to relay
        // to, so a single-service host does not expose an outbound request surface at all.
        if (options.RemoteServices.Count > 0)
        {
            group.Map(
                "/ui-api/proxy/{serviceName}/{*restPath}",
                (
                    string serviceName,
                    string? restPath,
                    HttpContext httpContext,
                    IHttpClientFactory httpClientFactory
                ) =>
                    ProxyRequestAsync(
                        serviceName,
                        restPath,
                        httpContext,
                        httpClientFactory,
                        options
                    )
            );
        }

        // Serve Static SPA Assets
        var indexHandler = ServeEmbeddedAsset(
            "index.html",
            "text/html; charset=utf-8",
            normalizedPrefix
        );
        group.MapGet("/", indexHandler);
        group.MapGet("/index.html", indexHandler);
        group.MapGet("/app.css", ServeEmbeddedAsset("app.css", "text/css; charset=utf-8"));
        group.MapGet(
            "/app.js",
            ServeEmbeddedAsset("app.js", "application/javascript; charset=utf-8")
        );

        return endpoints;
    }

    /// <summary>
    /// The local management API is addressed by path relative to the host root; remote services
    /// carry their own absolute URL instead.
    /// </summary>
    private static string ManagementApiPathForLocalHost(RatatoskrUIOptions options) =>
        options.IncludeLocalService ? "/" + options.LocalManagementApiPath.Trim('/') : string.Empty;

    private static void ValidateAuthorizationPolicy(
        IEndpointRouteBuilder endpoints,
        string? policyName
    )
    {
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return;
        }

        var authOptions = endpoints
            .ServiceProvider.GetRequiredService<IOptions<AuthorizationOptions>>()
            .Value;
        if (authOptions.GetPolicy(policyName) is null)
        {
            throw new InvalidOperationException(
                $"Authorization policy '{policyName}' is not registered. Call services.AddAuthorization() before MapRatatoskrUI."
            );
        }
    }

    /// <summary>
    /// Relaying to remote services needs an <see cref="IHttpClientFactory"/>. Surface a missing
    /// registration at startup rather than as a DI failure on the first proxied request.
    /// </summary>
    private static void ValidateProxyPrerequisites(
        IEndpointRouteBuilder endpoints,
        RatatoskrUIOptions options
    )
    {
        if (options.RemoteServices.Count == 0)
        {
            return;
        }

        if (endpoints.ServiceProvider.GetService<IHttpClientFactory>() is null)
        {
            throw new InvalidOperationException(
                "Remote Ratatoskr services are registered but no IHttpClientFactory is available. "
                    + "Call services.AddRatatoskrUI(...) (which registers one) instead of registering RatatoskrUIOptions directly."
            );
        }
    }

    private static async Task<IResult> ProxyRequestAsync(
        string serviceName,
        string? restPath,
        HttpContext httpContext,
        IHttpClientFactory httpClientFactory,
        RatatoskrUIOptions options
    )
    {
        if (options.FindService(serviceName) is not { } serviceEndpoint)
        {
            var known = string.Join(", ", options.RemoteServices.Select(s => $"'{s.Name}'"));
            return Results.NotFound(
                new
                {
                    message = $"No remote service named '{serviceName}' is registered. Known services: {known}.",
                }
            );
        }

        var targetBase = serviceEndpoint.ManagementApiUrl.TrimEnd('/');
        var relativePath = restPath?.TrimStart('/') ?? string.Empty;
        var targetUrl = $"{targetBase}/{relativePath}{httpContext.Request.QueryString}";

        using var client = httpClientFactory.CreateClient(ProxyHttpClientName);
        using var proxyReq = new HttpRequestMessage(
            new HttpMethod(httpContext.Request.Method),
            targetUrl
        );

        if (
            httpContext.Request.Headers.Authorization.Count > 0
            && AuthenticationHeaderValue.TryParse(
                httpContext.Request.Headers.Authorization.ToString(),
                out var authHeader
            )
        )
        {
            proxyReq.Headers.Authorization = authHeader;
        }

        // Forward the body whenever there is one rather than for a fixed method allow-list:
        // the bulk delete endpoints take their ids in a DELETE body, so a POST/PUT-only rule
        // silently turned "delete these ids" into a malformed request against a remote service.
        if (HasBody(httpContext.Request))
        {
            proxyReq.Content = new StreamContent(httpContext.Request.Body);
            if (httpContext.Request.ContentType is { } contentType)
            {
                proxyReq.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
        }

        try
        {
            using var resp = await client.SendAsync(proxyReq, httpContext.RequestAborted);
            httpContext.Response.StatusCode = (int)resp.StatusCode;
            httpContext.Response.ContentType =
                resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            await resp.Content.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
            return Results.Empty;
        }
        catch (Exception ex)
        {
            return Results.Problem(
                $"Failed to proxy request to remote service '{serviceEndpoint.Name}': {ex.Message}"
            );
        }
    }

    private static bool HasBody(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        // A chunked request has no Content-Length, so fall back to the transfer encoding.
        return request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0;
    }

    private static Func<HttpContext, IResult> ServeEmbeddedAsset(
        string resourceName,
        string contentType,
        string? routePrefixToInject = null
    )
    {
        return httpContext =>
        {
            var fileInfo = FileProvider.GetFileInfo(resourceName);
            if (!fileInfo.Exists)
            {
                return Results.NotFound($"Static asset '{resourceName}' not found.");
            }

            using var stream = fileInfo.CreateReadStream();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            if (routePrefixToInject is not null)
            {
                var pathBase = httpContext.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
                content = content.Replace(
                    BasePathToken,
                    pathBase + routePrefixToInject,
                    StringComparison.Ordinal
                );
            }

            return Results.Content(content, contentType);
        };
    }
}
