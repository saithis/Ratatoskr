using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Ratatoskr.UI.Proxy;

namespace Ratatoskr.UI;

/// <summary>
/// Extension methods for wiring the Ratatoskr Management UI into an ASP.NET Core application.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Registers Ratatoskr UI middleware.
    /// <para>
    /// <b>Call order matters:</b> place this call <em>before</em>
    /// <c>UseAuthentication()</c>, <c>UseAuthorization()</c>, and <c>UseRouting()</c> so that
    /// the local backend dispatcher can capture the full downstream pipeline.
    /// </para>
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="configure">Callback to configure backends and the base path.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    public static IApplicationBuilder UseRatatoskrUi(
        this IApplicationBuilder app,
        Action<RatatoskrUiOptions> configure)
    {
        var options = new RatatoskrUiOptions();
        configure(options);

        // Store the options in a well-known singleton so MapRatatoskrUiRoutes can access them.
        var optionsHolder = app.ApplicationServices.GetRequiredService<RatatoskrUiOptionsHolder>();
        optionsHolder.Options = options;

        var pipelineHolder = app.ApplicationServices.GetRequiredService<LocalPipelineHolder>();

        // Capture the downstream pipeline (everything registered after this point) on the first
        // real request. Placing UseRatatoskrUi early ensures the captured pipeline includes
        // authentication, authorization, routing, and endpoint middleware.
        app.Use((context, next) =>
        {
            pipelineHolder.TrySet(next);
            return next(context);
        });

        // Serve Angular SPA static files from the embedded wwwroot (production build only).
        // In development the Angular dev server serves its own assets via the proxy.
        var basePath = options.BasePath.TrimEnd('/');
        if (TryGetWwwrootProvider() is { } wwwrootProvider)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = basePath,
                FileProvider = wwwrootProvider
            });
        }

        return app;
    }

    /// <summary>
    /// Calls <see cref="UseRatatoskrUi"/> only when <see cref="AddRatatoskrUi"/> has been
    /// called with pre-configured options stored in the DI container. This is a no-op when
    /// the Ratatoskr UI is not configured — useful in shared test hosts.
    /// </summary>
    /// <remarks>
    /// Use this in hosts where the UI is optional (e.g. test hosts shared between UI and
    /// non-UI tests). Standard production apps should call <see cref="UseRatatoskrUi"/> directly.
    /// </remarks>
    public static IApplicationBuilder UseRatatoskrUiIfConfigured(this IApplicationBuilder app)
    {
        var holder = app.ApplicationServices.GetService<RatatoskrUiOptionsHolder>();
        if (holder?.Options is null) return app;

        var options = holder.Options;
        var pipelineHolder = app.ApplicationServices.GetRequiredService<LocalPipelineHolder>();

        app.Use((context, next) =>
        {
            pipelineHolder.TrySet(next);
            return next(context);
        });

        var basePath = options.BasePath.TrimEnd('/');
        if (TryGetWwwrootProvider() is { } wwwrootProvider)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = basePath,
                FileProvider = wwwrootProvider
            });
        }

        return app;
    }

    /// <summary>
    /// Maps all Ratatoskr Management UI proxy routes.
    /// Requires that <see cref="AddRatatoskrUi"/> was called on the service collection and
    /// <see cref="UseRatatoskrUi"/> was called before this method.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrUiRoutes(this IEndpointRouteBuilder endpoints)
    {
        var optionsHolder = endpoints.ServiceProvider.GetRequiredService<RatatoskrUiOptionsHolder>();
        var options = optionsHolder.Options
            ?? throw new InvalidOperationException(
                "UseRatatoskrUi() must be called before MapRatatoskrUiRoutes().");

        return MapRatatoskrUiRoutes(endpoints, options);
    }

    /// <summary>
    /// Maps Ratatoskr Management UI proxy routes only when the UI is configured.
    /// This is a no-op when <see cref="AddRatatoskrUi"/> was not called — useful in shared test hosts.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrUiRoutesIfConfigured(
        this IEndpointRouteBuilder endpoints)
    {
        var holder = endpoints.ServiceProvider.GetService<RatatoskrUiOptionsHolder>();
        if (holder?.Options is null) return endpoints;
        return MapRatatoskrUiRoutes(endpoints, holder.Options);
    }

    /// <summary>
    /// Maps all Ratatoskr Management UI proxy routes using the supplied <paramref name="options"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrUiRoutes(
        this IEndpointRouteBuilder endpoints,
        RatatoskrUiOptions options)
    {
        var basePath = options.BasePath.TrimEnd('/');

        // Determine whether to require authorization on the UI routes.
        bool hasPolicy = !string.IsNullOrWhiteSpace(options.PolicyName);

        RouteGroupBuilder AuthGroup(RouteGroupBuilder group) =>
            hasPolicy ? group.RequireAuthorization(options.PolicyName) : group;

        var apiGroup = AuthGroup(endpoints.MapGroup($"{basePath}/api/v1"));

        // GET /ratatoskr/api/v1/backends — list registered backends
        apiGroup.MapGet("/backends",
            () => Results.Ok(options.Backends.Select(b => new BackendDto(b.Name, b.IsLocal))));

        // GET /ratatoskr/api/v1/dashboard — parallel health fan-out across all backends
        apiGroup.MapGet("/dashboard",
            async (IHttpClientFactory http, LocalBackendDispatcher local,
                   IHttpContextAccessor accessor, CancellationToken ct) =>
            {
                var tasks = options.Backends.Select(b =>
                    FetchBackendHealthAsync(b, http, local, accessor.HttpContext!, basePath, ct));
                var results = await Task.WhenAll(tasks);
                return Results.Ok(new DashboardDto(results));
            });

        // ANY /ratatoskr/api/v1/backends/{name}/{**rest} — transparent passthrough to backend
        apiGroup.Map("/backends/{name}/{**rest}",
            async (string name, string? rest, HttpContext ctx,
                   IHttpClientFactory http, LocalBackendDispatcher local,
                   CancellationToken ct) =>
            {
                var backend = options.Backends
                    .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (backend is null)
                    return Results.Problem(
                        title: "Backend not found",
                        detail: $"Backend '{name}' is not registered.",
                        statusCode: StatusCodes.Status404NotFound);

                var targetPath = $"/ratatoskr/api/v1/{rest}";

                HttpResponseMessage backendResponse;
                if (backend.IsLocal)
                {
                    backendResponse = await local.DispatchAsync(ctx, targetPath, ct);
                }
                else
                {
                    var client = http.CreateClient($"Ratatoskr.UI.{backend.Name}");
                    client.BaseAddress = new Uri(backend.BaseUrl!);
                    var request = new HttpRequestMessage(
                        new HttpMethod(ctx.Request.Method),
                        targetPath + ctx.Request.QueryString.Value);

                    if (backend.AuthDelegate is not null)
                        await backend.AuthDelegate(request);

                    // Forward request body for mutating methods.
                    if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
                    {
                        request.Content = new StreamContent(ctx.Request.Body);
                        if (ctx.Request.ContentType is { } ct2)
                            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ct2);
                    }

                    backendResponse = await client.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, ct);
                }

                ctx.Response.StatusCode = (int)backendResponse.StatusCode;
                foreach (var (key, value) in backendResponse.Headers)
                    ctx.Response.Headers.Append(key, value.ToArray());
                foreach (var (key, value) in backendResponse.Content.Headers)
                    ctx.Response.Headers.Append(key, value.ToArray());

                await backendResponse.Content.CopyToAsync(ctx.Response.Body, ct);
                return Results.Empty;
            });

        // SPA fallback: serve index.html for all non-API routes under basePath.
        // Clients using HashLocationStrategy don't need this, but it's good for direct navigation.
        // In development (no Angular build) a 404 is returned — the dev server handles the SPA.
        endpoints.MapFallback($"{basePath}/{{**path}}", async (HttpContext ctx) =>
        {
            var indexFile = TryGetWwwrootProvider()?.GetFileInfo("index.html");
            if (indexFile is not { Exists: true })
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await using var stream = indexFile.CreateReadStream();
            await stream.CopyToAsync(ctx.Response.Body);
        });

        return endpoints;
    }

    private static async Task<BackendHealthDto> FetchBackendHealthAsync(
        BackendRegistration backend,
        IHttpClientFactory http,
        LocalBackendDispatcher local,
        HttpContext ctx,
        string basePath,
        CancellationToken ct)
    {
        try
        {
            // Reuse the management health endpoint exposed by each backend.
            const string healthPath = "/ratatoskr/api/v1/efcore/contexts";

            HttpResponseMessage response;
            if (backend.IsLocal)
            {
                response = await local.DispatchAsync(ctx, healthPath, ct);
            }
            else
            {
                var client = http.CreateClient($"Ratatoskr.UI.{backend.Name}");
                client.BaseAddress = new Uri(backend.BaseUrl!);
                var request = new HttpRequestMessage(HttpMethod.Get, healthPath);
                if (backend.AuthDelegate is not null)
                    await backend.AuthDelegate(request);
                response = await client.SendAsync(request, ct);
            }

            var ok = response.IsSuccessStatusCode;
            return new BackendHealthDto(backend.Name, backend.IsLocal, ok, Error: null);
        }
        catch (Exception ex)
        {
            return new BackendHealthDto(backend.Name, backend.IsLocal, Healthy: false, Error: ex.Message);
        }
    }

    // DTOs used only by these route handlers (no shared model needed)
    private sealed record BackendDto(string Name, bool IsLocal);
    private sealed record BackendHealthDto(string Name, bool IsLocal, bool Healthy, string? Error);
    private sealed record DashboardDto(IEnumerable<BackendHealthDto> Backends);

    /// <summary>
    /// Returns the embedded wwwroot provider, or <see langword="null"/> if no embedded manifest
    /// exists (i.e. Angular was never built — expected in development/debug builds).
    /// </summary>
    private static ManifestEmbeddedFileProvider? TryGetWwwrootProvider()
    {
        try
        {
            var assembly = typeof(ApplicationBuilderExtensions).Assembly;
            var provider = new ManifestEmbeddedFileProvider(assembly, "wwwroot");
            // Verify there is actually content (not just an empty manifest)
            return provider.GetDirectoryContents("").Exists ? provider : null;
        }
        catch (InvalidOperationException)
        {
            // No embedded manifest — Angular has not been built yet.
            return null;
        }
    }
}
