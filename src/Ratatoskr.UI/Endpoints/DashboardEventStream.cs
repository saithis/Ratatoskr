using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;

namespace Ratatoskr.UI.Endpoints;

/// <summary>
/// Streams registry changes to one connected browser as server-sent events.
/// </summary>
/// <remarks>
/// One connection, one bounded channel, one writer loop. The channel holds a single slot and drops
/// the older value, so a burst of heartbeats from fifty replicas collapses into one redraw and a
/// browser that cannot keep up costs a constant amount of memory rather than a growing queue.
/// <para>
/// Each wake-up sends a full snapshot rather than a delta. Coalescing means some updates were
/// discarded by design, and a client that applied deltas would end up showing a state that never
/// existed.
/// </para>
/// </remarks>
internal static class DashboardEventStream
{
    /// <summary>
    /// How long the stream waits before sending a comment line to keep the connection open.
    /// Proxies routinely drop an idle event-stream, and a silently dead stream looks exactly like
    /// a healthy fleet with nothing happening.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public static async Task WriteAsync(
        HttpContext http,
        ServiceRegistry registry,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var updates = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );

        void OnServiceUpdated(object? sender, ServiceAnnouncementEventArgs args) =>
            updates.Writer.TryWrite(0);

        registry.ServiceUpdated += OnServiceUpdated;

        try
        {
            // Everything below runs on this one task, so nothing else can write to the response
            // concurrently — two writers on an HttpResponse produce interleaved, unparseable
            // frames rather than an error.
            await WriteSnapshotAsync(http, registry, cancellationToken);

            // The waiter is created once and renewed only after it completes. Registering a fresh
            // one on every keep-alive tick would pile up channel waiters for the life of the
            // connection.
            var pending = updates.Reader.WaitToReadAsync(cancellationToken).AsTask();

            while (!cancellationToken.IsCancellationRequested)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                var idle = Task.Delay(KeepAliveInterval, timeProvider, keepAlive.Token);

                if (await Task.WhenAny(pending, idle) == pending)
                {
                    await keepAlive.CancelAsync();

                    if (!await pending)
                    {
                        break;
                    }

                    // Drain: several announcements may have arrived while the last snapshot was
                    // being written, and they are all answered by the next one.
                    while (updates.Reader.TryRead(out _)) { }
                    await WriteSnapshotAsync(http, registry, cancellationToken);
                    pending = updates.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }
                else
                {
                    await http.Response.WriteAsync(":keep-alive\n\n", cancellationToken);
                    await http.Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The browser navigated away or the host is shutting down. Both are normal.
        }
        finally
        {
            registry.ServiceUpdated -= OnServiceUpdated;
            updates.Writer.TryComplete();
        }
    }

    private static async Task WriteSnapshotAsync(
        HttpContext http,
        ServiceRegistry registry,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.Serialize(registry.GetServices(), ManagementJson.Options);
        await http.Response.WriteAsync($"event: services\ndata: {payload}\n\n", cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }
}
