using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.UI;

/// <summary>
/// Extension methods for configuring and mounting the Ratatoskr management UI dashboard.
/// </summary>
public static class RatatoskrUiEndpointExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Assembly UiAssembly = typeof(RatatoskrUiEndpointExtensions).Assembly;

    /// <summary>
    /// Adds Ratatoskr UI services, background broker listeners, and active service tracking.
    /// </summary>
    public static IServiceCollection AddRatatoskrUI(
        this IServiceCollection services,
        Action<RatatoskrUiOptions>? configure = null
    )
    {
        services.AddOptions<RatatoskrUiOptions>();
        if (configure != null)
        {
            services.Configure(configure);
        }

        return services;
    }

    /// <summary>
    /// Maps the Ratatoskr management UI dashboard and proxy API endpoints.
    /// Requires a registered authorization policy name to ensure secure access.
    /// </summary>
    public static IEndpointRouteBuilder MapRatatoskrUI(
        this IEndpointRouteBuilder endpoints,
        string policyName,
        string basePath = "/ratatoskr"
    ) => endpoints.MapRatatoskrUI(
        new RatatoskrUiAuthorizationPolicies(policyName, policyName, policyName, policyName, policyName),
        basePath);

    /// <summary>Maps the UI with separate policies for read, sensitive-data, and mutation capabilities.</summary>
    public static IEndpointRouteBuilder MapRatatoskrUI(
        this IEndpointRouteBuilder endpoints,
        RatatoskrUiAuthorizationPolicies policies,
        string basePath = "/ratatoskr"
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        basePath = basePath.TrimEnd('/');

        // Validate policy existence at startup
        var authOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var policyNames = new[] { policies.ViewMetadata, policies.ViewPayloads, policies.RequeueMessages, policies.DeleteMessages, policies.BulkOperations };
        if (policyNames.Any(string.IsNullOrWhiteSpace) || policyNames.Any(name => authOptions.GetPolicy(name) is null))
        {
            throw new InvalidOperationException(
                "Every Ratatoskr UI authorization policy must be registered. "
                    + "Call services.AddAuthorization() before calling MapRatatoskrUI."
            );
        }

        var group = endpoints.MapGroup(basePath)
            .RequireAuthorization(policies.ViewMetadata)
            .DisableAntiforgery();

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'";
            context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            return await next(context);
        });

        // ── Static Web Assets ────────────────────────────────────────────────
        group.MapGet("/", ServeIndexHtml);
        group.MapGet("/index.html", ServeIndexHtml);

        group.MapGet("/css/{filename}", (string filename) =>
            ServeEmbeddedFile($"css.{filename}", "text/css"));

        group.MapGet("/js/{filename}", (string filename) =>
            ServeEmbeddedFile($"js.{filename}", "application/javascript"));

        // ── Server-Sent Events (SSE) ─────────────────────────────────────────
        group.MapGet("/api/events", async (
            HttpContext context,
            IServiceCatalog catalog,
            CancellationToken ct
        ) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var updates = Channel.CreateBounded<ServiceHeartbeat>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            void OnServiceUpdate(object? sender, ServiceHeartbeatEventArgs args)
            {
                // The bounded, latest-value queue coalesces heartbeat bursts for slow browsers.
                updates.Writer.TryWrite(args.Heartbeat);
            }

            catalog.ServiceUpdated += OnServiceUpdate;
            try
            {
                await WriteSnapshotAsync(context, catalog, ct);

                while (true)
                {
                    var update = updates.Reader.WaitToReadAsync(ct).AsTask();
                    var heartbeat = Task.Delay(TimeSpan.FromSeconds(15), ct);
                    var completed = await Task.WhenAny(update, heartbeat);
                    if (completed == update && await update)
                    {
                        while (updates.Reader.TryRead(out _)) { }
                        // A snapshot avoids inconsistent browser state when updates were coalesced.
                        await WriteSnapshotAsync(context, catalog, ct);
                    }
                    else if (completed == heartbeat)
                    {
                        await context.Response.WriteAsync(":\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
            finally
            {
                catalog.ServiceUpdated -= OnServiceUpdate;
                updates.Writer.TryComplete();
            }
        });

        // ── Management Proxy APIs ────────────────────────────────────────────
        var api = group.MapGroup("/api");

        api.MapGet("/services", (IServiceCatalog catalog) =>
            TypedResults.Ok(catalog.GetAllServices()));

        api.MapGet("/services/{serviceName}", (string serviceName, IServiceCatalog catalog) =>
        {
            var detail = catalog.GetService(serviceName);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        });

        api.MapGet("/services/{serviceName}/stats", async (
            string serviceName,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<object, ServiceHeartbeat>(serviceName, null, "GetStats", new { }, ct);
            return res != null ? Results.Ok(res) : Results.NotFound();
        });

        // Outbox
        api.MapGet("/services/{serviceName}/contexts/{contextName}/outbox", async (
            string serviceName,
            string contextName,
            string? status,
            string? cursor,
            int? limit,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var req = new GetOutboxMessagesRequest(status ?? "Poisoned", cursor, limit ?? 20);
            var res = await client.ExecuteAsync<GetOutboxMessagesRequest, CursorPagedResult<OutboxItemDto>>(
                serviceName, contextName, "GetOutbox", req, ct);
            return Results.Ok(res);
        });

        api.MapGet("/services/{serviceName}/contexts/{contextName}/outbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<GetOutboxDetailRequest, OutboxDetailDto>(
                serviceName, contextName, "GetOutboxDetail", new GetOutboxDetailRequest(id), ct);
            return res != null ? Results.Ok(res) : Results.NotFound();
        }).RequireAuthorization(policies.ViewPayloads);

        api.MapPost("/services/{serviceName}/contexts/{contextName}/outbox/{id:guid}/requeue", async (
            string serviceName,
            string contextName,
            Guid id,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<RequeueOutboxRequest, RequeueResultDto>(
                serviceName, contextName, "RequeueOutbox", new RequeueOutboxRequest(id), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.RequeueMessages);

        api.MapPost("/services/{serviceName}/contexts/{contextName}/outbox/bulk-requeue", async (
            string serviceName,
            string contextName,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkRequeueOutboxRequest, RequeueResultDto>(
                serviceName, contextName, "BulkRequeueOutbox", new BulkRequeueOutboxRequest(), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.BulkOperations);

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/outbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<DeleteOutboxRequest, DeleteResultDto>(
                serviceName, contextName, "DeleteOutbox", new DeleteOutboxRequest(id), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.DeleteMessages);

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/outbox/bulk-delete", async (
            string serviceName,
            string contextName,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkDeleteOutboxRequest, DeleteResultDto>(
                serviceName, contextName, "BulkDeleteOutbox", new BulkDeleteOutboxRequest(), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.BulkOperations);

        // Inbox
        api.MapGet("/services/{serviceName}/contexts/{contextName}/inbox", async (
            string serviceName,
            string contextName,
            string? status,
            string? cursor,
            int? limit,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var req = new GetInboxMessagesRequest(status ?? "Poisoned", cursor, limit ?? 20);
            var res = await client.ExecuteAsync<GetInboxMessagesRequest, CursorPagedResult<InboxItemDto>>(
                serviceName, contextName, "GetInbox", req, ct);
            return Results.Ok(res);
        });

        api.MapGet("/services/{serviceName}/contexts/{contextName}/inbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<GetInboxDetailRequest, InboxDetailDto>(
                serviceName, contextName, "GetInboxDetail", new GetInboxDetailRequest(id), ct);
            return res != null ? Results.Ok(res) : Results.NotFound();
        }).RequireAuthorization(policies.ViewPayloads);

        api.MapPost("/services/{serviceName}/contexts/{contextName}/inbox/{id:guid}/requeue", async (
            string serviceName,
            string contextName,
            Guid id,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<RequeueInboxHandlerRequest, RequeueResultDto>(
                serviceName, contextName, "RequeueInboxHandler", new RequeueInboxHandlerRequest(id), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.RequeueMessages);

        api.MapPost("/services/{serviceName}/contexts/{contextName}/inbox/message/{messageId}/requeue", async (
            string serviceName,
            string contextName,
            string messageId,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<RequeueInboxMessageRequest, RequeueResultDto>(
                serviceName, contextName, "RequeueInboxMessage", new RequeueInboxMessageRequest(messageId), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.RequeueMessages);

        api.MapPost("/services/{serviceName}/contexts/{contextName}/inbox/bulk-requeue", async (
            string serviceName,
            string contextName,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkRequeueInboxRequest, RequeueResultDto>(
                serviceName, contextName, "BulkRequeueInbox", new BulkRequeueInboxRequest(), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.BulkOperations);

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/inbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<DeleteInboxHandlerRequest, DeleteResultDto>(
                serviceName, contextName, "DeleteInboxHandler", new DeleteInboxHandlerRequest(id), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.DeleteMessages);

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/inbox/bulk-delete", async (
            string serviceName,
            string contextName,
            IManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkDeleteInboxRequest, DeleteResultDto>(
                serviceName, contextName, "BulkDeleteInbox", new BulkDeleteInboxRequest(), ct);
            return Results.Ok(res);
        }).RequireAuthorization(policies.BulkOperations);

        return endpoints;
    }

    private static IResult ServeIndexHtml() =>
        ServeEmbeddedFile("index.html", "text/html; charset=utf-8");

    private static async Task WriteSnapshotAsync(HttpContext context, IServiceCatalog catalog, CancellationToken cancellationToken)
    {
        var services = JsonSerializer.Serialize(catalog.GetAllServices(), JsonOptions);
        await context.Response.WriteAsync($"event: snapshot\ndata: {services}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "Stream is transferred to IResult which disposes it upon HTTP response completion")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP003:Dispose previous before re-assigning", Justification = "Fallback stream retrieval")]
    private static IResult ServeEmbeddedFile(string relativePath, string contentType)
    {
        var resourceName = $"Ratatoskr.UI.wwwroot.{relativePath}";
        var stream = UiAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Fallback check in case embedded path separators differ
            var matches = UiAssembly.GetManifestResourceNames();
            var matched = matches.FirstOrDefault(m => m.EndsWith(relativePath, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                stream = UiAssembly.GetManifestResourceStream(matched);
            }
        }

        if (stream is null)
        {
            return Results.NotFound($"Static resource '{relativePath}' not found in assembly.");
        }

        return Results.Stream(stream, contentType);
    }
}
