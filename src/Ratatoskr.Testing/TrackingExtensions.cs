using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ratatoskr.Testing;

/// <summary>
/// Extension methods for creating message tracking sessions in integration tests.
/// </summary>
public static class TrackingExtensions
{
    /// <summary>
    /// Creates a new message tracking session with a unique trace ID.
    /// All messages published within the session scope are correlated by trace ID,
    /// enabling parallel test isolation.
    /// </summary>
    /// <param name="services">The service provider (typically from WebApplicationFactory.Services)</param>
    /// <param name="defaultTimeout">Default timeout for wait operations (default: 10 seconds)</param>
    public static MessageTrackingSession CreateTrackingSession(
        this IServiceProvider services,
        TimeSpan? defaultTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        var tracker = services.GetRequiredService<MessageTracker>();
        return new MessageTrackingSession(tracker, defaultTimeout);
    }

    /// <summary>
    /// Creates a new message tracking session with a unique trace ID.
    /// </summary>
    public static MessageTrackingSession CreateTrackingSession(
        this IHost host,
        TimeSpan? defaultTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Services.CreateTrackingSession(defaultTimeout);
    }

    /// <summary>
    /// Creates an activity tracker for the action-based API pattern.
    /// Use <see cref="ActivityTracker.ExecuteAndWaitAsync"/> to wrap test actions.
    /// </summary>
    public static ActivityTracker TrackActivity(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var tracker = services.GetRequiredService<MessageTracker>();
        return new ActivityTracker(services, tracker);
    }

    /// <summary>
    /// Creates an activity tracker for the action-based API pattern.
    /// </summary>
    public static ActivityTracker TrackActivity(this IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Services.TrackActivity();
    }
}
