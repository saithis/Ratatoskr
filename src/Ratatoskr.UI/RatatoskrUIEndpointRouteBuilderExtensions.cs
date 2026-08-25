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

    /// <summary>
    /// Maps the Ratatoskr Management Dashboard web UI under <paramref name="routePrefix"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrUI(
        this IEndpointRouteBuilder endpoints,
        string routePrefix = "/ratatoskr",
        string? policyName = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);

        var options =
            endpoints.ServiceProvider.GetService<RatatoskrUIOptions>() ?? new RatatoskrUIOptions();
        var normalizedPrefix = routePrefix.TrimEnd('/');

        ValidateAuthorizationPolicy(endpoints, policyName);

        var group = endpoints.MapGroup(normalizedPrefix);
        if (!string.IsNullOrWhiteSpace(policyName))
        {
            group.RequireAuthorization(policyName);
        }
        group.DisableAntiforgery();

        // UI Config Endpoint
        group.MapGet(
            "/ui-api/config",
            (HttpContext httpContext, RatatoskrUIOptions? opt) =>
            {
                // Everything the browser sees has to be rooted at PathBase, otherwise the UI
                // breaks as soon as the host is mounted behind a reverse proxy sub-path.
                var pathBase = httpContext.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
                return TypedResults.Ok(
                    new
                    {
                        title = (opt ?? options).Title,
                        routePrefix = pathBase + normalizedPrefix,
                        pollingIntervalMs = (opt ?? options).PollingIntervalMs,
                        enablePayloadEditing = (opt ?? options).EnablePayloadEditing,
                        defaultBasePath = pathBase + "/ratatoskr/api/v1",
                        remoteServices = (opt ?? options).RemoteServices,
                    }
                );
            }
        );

        // UI Multi-Service Relay Proxy Endpoint
        group.Map("/ui-api/proxy/{serviceIndex:int}/{*restPath}", ProxyRequestAsync);

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

    private static async Task<IResult> ProxyRequestAsync(
        int serviceIndex,
        string? restPath,
        HttpContext httpContext,
        IHttpClientFactory httpClientFactory,
        RatatoskrUIOptions opt
    )
    {
        if (serviceIndex < 0 || serviceIndex >= opt.RemoteServices.Count)
        {
            return Results.NotFound(
                new { message = $"Remote service index {serviceIndex} not found." }
            );
        }

        var serviceEndpoint = opt.RemoteServices[serviceIndex];
        var targetBase = serviceEndpoint.ManagementApiUrl.TrimEnd('/');
        var relativePath = restPath?.TrimStart('/') ?? string.Empty;
        var targetUrl = $"{targetBase}/{relativePath}{httpContext.Request.QueryString}";

        using var client = httpClientFactory.CreateClient("RatatoskrUIProxy");
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

        if (
            HttpMethods.IsPost(httpContext.Request.Method)
            || HttpMethods.IsPut(httpContext.Request.Method)
        )
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
