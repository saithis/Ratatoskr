using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ratatoskr.Management.Contracts;
using Ratatoskr.UI.Client;

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

        services.TryAddSingleton<ActiveServiceRegistry>();
        services.TryAddSingleton<RatatoskrBrokerManagementClient>();
        services.TryAddSingleton<IRatatoskrBrokerManagementClient>(sp =>
            sp.GetRequiredService<RatatoskrBrokerManagementClient>());
        services.AddHostedService(sp => sp.GetRequiredService<RatatoskrBrokerManagementClient>());

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
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        basePath = basePath.TrimEnd('/');

        // Validate policy existence at startup
        var authOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        if (authOptions.GetPolicy(policyName) is null)
        {
            throw new InvalidOperationException(
                $"Authorization policy '{policyName}' is not registered. "
                    + "Call services.AddAuthorization() and define the policy before calling MapRatatoskrUI."
            );
        }

        var group = endpoints.MapGroup(basePath)
            .RequireAuthorization(policyName)
            .DisableAntiforgery();

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
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var tcs = new TaskCompletionSource();
            await using var reg = ct.Register(() => tcs.TrySetResult());

            void OnServiceUpdate(ServiceHeartbeat hb)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(hb, JsonOptions);
                        await context.Response.WriteAsync($"event: service-heartbeat\ndata: {json}\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                    catch
                    {
                        // ignore broken client pipe
                    }
                }, CancellationToken.None);
            }

            client.Registry.OnServiceUpdated += OnServiceUpdate;
            try
            {
                // Send initial snapshot
                var initialServices = JsonSerializer.Serialize(client.Registry.GetAllServices(), JsonOptions);
                await context.Response.WriteAsync($"event: snapshot\ndata: {initialServices}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    await context.Response.WriteAsync(":\n\n", ct); // keep-alive
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client disconnected
            }
            finally
            {
                client.Registry.OnServiceUpdated -= OnServiceUpdate;
            }
        });

        // ── Management Proxy APIs ────────────────────────────────────────────
        var api = group.MapGroup("/api");

        api.MapGet("/services", (IRatatoskrBrokerManagementClient client) =>
            TypedResults.Ok(client.Registry.GetAllServices()));

        api.MapGet("/services/{serviceName}", (string serviceName, IRatatoskrBrokerManagementClient client) =>
        {
            var detail = client.Registry.GetService(serviceName);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        });

        api.MapGet("/services/{serviceName}/stats", async (
            string serviceName,
            IRatatoskrBrokerManagementClient client,
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
            int? page,
            int? pageSize,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var req = new GetOutboxMessagesRequest(status ?? "Poisoned", page ?? 1, pageSize ?? 20);
            var res = await client.ExecuteAsync<GetOutboxMessagesRequest, PagedResult<OutboxItemDto>>(
                serviceName, contextName, "GetOutbox", req, ct);
            return Results.Ok(res);
        });

        api.MapGet("/services/{serviceName}/contexts/{contextName}/outbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<GetOutboxDetailRequest, OutboxDetailDto>(
                serviceName, contextName, "GetOutboxDetail", new GetOutboxDetailRequest(id), ct);
            return res != null ? Results.Ok(res) : Results.NotFound();
        });

        api.MapPost("/services/{serviceName}/contexts/{contextName}/outbox/{id:guid}/requeue", async (
            string serviceName,
            string contextName,
            Guid id,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<RequeueOutboxRequest, RequeueResultDto>(
                serviceName, contextName, "RequeueOutbox", new RequeueOutboxRequest(id), ct);
            return Results.Ok(res);
        });

        api.MapPost("/services/{serviceName}/contexts/{contextName}/outbox/bulk-requeue", async (
            string serviceName,
            string contextName,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkRequeueOutboxRequest, RequeueResultDto>(
                serviceName, contextName, "BulkRequeueOutbox", new BulkRequeueOutboxRequest(), ct);
            return Results.Ok(res);
        });

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/outbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<DeleteOutboxRequest, DeleteResultDto>(
                serviceName, contextName, "DeleteOutbox", new DeleteOutboxRequest(id), ct);
            return Results.Ok(res);
        });

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/outbox/bulk-delete", async (
            string serviceName,
            string contextName,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkDeleteOutboxRequest, DeleteResultDto>(
                serviceName, contextName, "BulkDeleteOutbox", new BulkDeleteOutboxRequest(), ct);
            return Results.Ok(res);
        });

        // Inbox
        api.MapGet("/services/{serviceName}/contexts/{contextName}/inbox", async (
            string serviceName,
            string contextName,
            string? status,
            int? page,
            int? pageSize,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var req = new GetInboxMessagesRequest(status ?? "Poisoned", page ?? 1, pageSize ?? 20);
            var res = await client.ExecuteAsync<GetInboxMessagesRequest, PagedResult<InboxItemDto>>(
                serviceName, contextName, "GetInbox", req, ct);
            return Results.Ok(res);
        });

        api.MapGet("/services/{serviceName}/contexts/{contextName}/inbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<GetInboxDetailRequest, InboxDetailDto>(
                serviceName, contextName, "GetInboxDetail", new GetInboxDetailRequest(id), ct);
            return res != null ? Results.Ok(res) : Results.NotFound();
        });

        api.MapPost("/services/{serviceName}/contexts/{contextName}/inbox/{id:guid}/requeue", async (
            string serviceName,
            string contextName,
            Guid id,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<RequeueInboxHandlerRequest, RequeueResultDto>(
                serviceName, contextName, "RequeueInboxHandler", new RequeueInboxHandlerRequest(id), ct);
            return Results.Ok(res);
        });

        api.MapPost("/services/{serviceName}/contexts/{contextName}/inbox/message/{messageId}/requeue", async (
            string serviceName,
            string contextName,
            string messageId,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<RequeueInboxMessageRequest, RequeueResultDto>(
                serviceName, contextName, "RequeueInboxMessage", new RequeueInboxMessageRequest(messageId), ct);
            return Results.Ok(res);
        });

        api.MapPost("/services/{serviceName}/contexts/{contextName}/inbox/bulk-requeue", async (
            string serviceName,
            string contextName,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkRequeueInboxRequest, RequeueResultDto>(
                serviceName, contextName, "BulkRequeueInbox", new BulkRequeueInboxRequest(), ct);
            return Results.Ok(res);
        });

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/inbox/{id:guid}", async (
            string serviceName,
            string contextName,
            Guid id,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<DeleteInboxHandlerRequest, DeleteResultDto>(
                serviceName, contextName, "DeleteInboxHandler", new DeleteInboxHandlerRequest(id), ct);
            return Results.Ok(res);
        });

        api.MapDelete("/services/{serviceName}/contexts/{contextName}/inbox/bulk-delete", async (
            string serviceName,
            string contextName,
            IRatatoskrBrokerManagementClient client,
            CancellationToken ct
        ) =>
        {
            var res = await client.ExecuteAsync<BulkDeleteInboxRequest, DeleteResultDto>(
                serviceName, contextName, "BulkDeleteInbox", new BulkDeleteInboxRequest(), ct);
            return Results.Ok(res);
        });

        return endpoints;
    }

    private static IResult ServeIndexHtml() =>
        ServeEmbeddedFile("index.html", "text/html; charset=utf-8");

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
