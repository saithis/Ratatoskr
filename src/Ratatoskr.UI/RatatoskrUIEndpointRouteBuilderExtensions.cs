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
            (RatatoskrUIOptions? opt) =>
                TypedResults.Ok(
                    new
                    {
                        title = (opt ?? options).Title,
                        routePrefix = normalizedPrefix,
                        pollingIntervalMs = (opt ?? options).PollingIntervalMs,
                        enablePayloadEditing = (opt ?? options).EnablePayloadEditing,
                        defaultBasePath = "/ratatoskr/api/v1",
                        remoteServices = (opt ?? options).RemoteServices,
                    }
                )
        );

        // UI Multi-Service Relay Proxy Endpoint
        group.Map("/ui-api/proxy/{serviceIndex:int}/{*restPath}", ProxyRequestAsync);

        // Serve Static SPA Assets
        group.MapGet("/", ServeEmbeddedAsset("index.html", "text/html; charset=utf-8"));
        group.MapGet("/index.html", ServeEmbeddedAsset("index.html", "text/html; charset=utf-8"));
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
        string contentType
    )
    {
        return _ =>
        {
            var fileInfo = FileProvider.GetFileInfo(resourceName);
            if (!fileInfo.Exists)
            {
                return Results.NotFound($"Static asset '{resourceName}' not found.");
            }

            using var stream = fileInfo.CreateReadStream();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            return Results.Content(content, contentType);
        };
    }
}
