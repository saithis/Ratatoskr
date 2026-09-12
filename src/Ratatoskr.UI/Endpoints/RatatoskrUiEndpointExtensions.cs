using System.Reflection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Http;
using Ratatoskr.Management.Registry;
using Ratatoskr.UI.Endpoints;
using Ratatoskr.UI.Store;

namespace Ratatoskr.UI;

/// <summary>Mounts the Ratatoskr management dashboard.</summary>
public static class RatatoskrUiEndpointExtensions
{
    private static readonly Assembly UiAssembly = typeof(RatatoskrUiEndpointExtensions).Assembly;

    private const string ContentSecurityPolicy =
        "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; "
        + "object-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'";

    /// <summary>Mounts the dashboard with a single authorization policy for every capability.</summary>
    public static IEndpointRouteBuilder MapRatatoskrUI(
        this IEndpointRouteBuilder endpoints,
        string policyName,
        string basePath = "/ratatoskr"
    ) => endpoints.MapRatatoskrUI(ManagementApiPolicies.Single(policyName), basePath);

    /// <summary>
    /// Mounts the dashboard with separate policies for reading metadata, reading payloads,
    /// requeueing, deleting and bulk operations.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrUI(
        this IEndpointRouteBuilder endpoints,
        ManagementApiPolicies policies,
        string basePath = "/ratatoskr"
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        policies.ValidateRegistered(endpoints);

        var strategy = endpoints.ServiceProvider.GetRequiredService<AuditingManagementDispatchStrategy>();

        var group = endpoints
            .MapGroup(basePath.TrimEnd('/'))
            .RequireAuthorization(policies.ViewMetadata);

        group.AddEndpointFilter(
            async (context, next) =>
            {
                var headers = context.HttpContext.Response.Headers;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                return await next(context);
            }
        );

        MapStaticAssets(group);
        MapDiscoveryApi(group);
        MapAuditApi(group);

        // The shared route tree, once per transport-and-service. Every operation the per-service
        // REST API exposes appears here too, because both are generated from the same method.
        ManagementApiRoutes.Map(
            group.MapGroup("/api/transports/{transport}/services/{serviceName}"),
            strategy,
            policies
        );

        return endpoints;
    }

    private static void MapStaticAssets(RouteGroupBuilder group)
    {
        group.MapGet("/", ServeIndex);
        group.MapGet("/index.html", ServeIndex);
        group.MapGet("/css/{filename}", (string filename) => Serve($"css.{filename}", "text/css"));
        group.MapGet(
            "/js/{filename}",
            (string filename) => Serve($"js.{filename}", "text/javascript")
        );
    }

    private static void MapDiscoveryApi(RouteGroupBuilder group)
    {
        var api = group.MapGroup("/api");

        // Every management response goes out through ManagementJson so the dashboard sees one JSON
        // dialect: enums as names, nulls omitted, the same shapes the transports carry.
        api.MapGet(
            "/antiforgery",
            (HttpContext http, IAntiforgery antiforgery) =>
            {
                // Cookie-authenticated callers need a token to mutate anything. Handing it out
                // here, rather than embedding it in the page, keeps index.html cacheable and
                // lets a long-lived tab refresh the token without a reload.
                var tokens = antiforgery.GetAndStoreTokens(http);
                return Results.Json(
                    new AntiforgeryTokenResponse(
                        tokens.HeaderName ?? "RequestVerificationToken",
                        tokens.RequestToken
                    ),
                    ManagementJson.Options
                );
            }
        );

        api.MapGet(
            "/transports",
            (IManagementTransportRegistry transports) =>
                Results.Json(transports.TransportNames, ManagementJson.Options)
        );

        api.MapGet(
            "/services",
            (ServiceRegistry registry) => Results.Json(registry.GetServices(), ManagementJson.Options)
        );

        api.MapGet(
            "/transports/{transport}/services/{serviceName}",
            (string transport, string serviceName, ServiceRegistry registry) =>
                registry.GetService(transport, serviceName) is { } detail
                    ? Results.Json(detail, ManagementJson.Options)
                    : Results.NotFound()
        );

        api.MapGet(
            "/events",
            (HttpContext http, ServiceRegistry registry, TimeProvider timeProvider) =>
                DashboardEventStream.WriteAsync(http, registry, timeProvider, http.RequestAborted)
        );
    }

    private static void MapAuditApi(RouteGroupBuilder group)
    {
        group
            .MapGroup("/api")
            .MapGet(
                "/audit",
                async (
                    RatatoskrDashboardDbContext db,
                    string? service,
                    string? transport,
                    int? limit,
                    CancellationToken cancellationToken
                ) =>
                {
                    var query = db.AuditEntries.AsNoTracking().AsQueryable();

                    if (!string.IsNullOrWhiteSpace(transport))
                    {
                        query = query.Where(entry => entry.TransportName == transport);
                    }

                    if (!string.IsNullOrWhiteSpace(service))
                    {
                        query = query.Where(entry => entry.ServiceName == service);
                    }

                    var entries = await query
                        .OrderByDescending(entry => entry.StartedAt)
                        .Take(Math.Clamp(limit ?? 100, 1, 500))
                        .ToListAsync(cancellationToken);

                    return Results.Json(entries, ManagementJson.Options);
                }
            );
    }

    private static IResult ServeIndex() => Serve("index.html", "text/html; charset=utf-8");

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "IDisposableAnalyzers.Correctness",
        "IDISP001:Dispose created",
        Justification = "Ownership of the stream passes to the IResult, which disposes it when the response completes."
    )]
    private static IResult Serve(string relativePath, string contentType)
    {
        // Only exact matches: a filename from the route must never be able to reach an arbitrary
        // manifest resource, so there is deliberately no suffix-matching fallback here.
        var stream = UiAssembly.GetManifestResourceStream($"Ratatoskr.UI.wwwroot.{relativePath}");
        return stream is null
            ? Results.NotFound()
            : Results.Stream(stream, contentType);
    }

    private sealed record AntiforgeryTokenResponse(string HeaderName, string? RequestToken);
}
