using System.Diagnostics;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// A per-test message tracking session that creates a unique trace ID
/// for correlating messages. All messages published within this session's
/// scope are tagged with the session's trace ID, enabling parallel test isolation.
/// </summary>
public sealed class MessageTrackingSession : IAsyncDisposable
{
    private readonly MessageTracker _tracker;
    private readonly Activity _activity;
    private readonly Activity? _previousActivity;
    private readonly TimeSpan _defaultTimeout;
    private readonly Dictionary<MessageStage, MessageCollection> _collections = new();
    private bool _disposed;

    internal MessageTrackingSession(MessageTracker tracker, TimeSpan? defaultTimeout = null)
    {
        _tracker = tracker;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(10);

        // Save and clear current activity to ensure a fresh trace ID
        _previousActivity = Activity.Current;
        Activity.Current = null;

        _activity = new Activity("Ratatoskr.Test");
        _activity.SetIdFormat(ActivityIdFormat.W3C);
        _activity.Start();
    }

    /// <summary>
    /// The unique trace ID for this session.
    /// </summary>
    public string TraceId => _activity.TraceId.ToString();

    /// <summary>
    /// Messages captured at the Published stage (after PublishDirectAsync).
    /// </summary>
    public MessageCollection Published => GetCollection(MessageStage.Published);

    /// <summary>
    /// Messages captured at the Sent stage (bytes on the wire).
    /// </summary>
    public MessageCollection Sent => GetCollection(MessageStage.Sent);

    /// <summary>
    /// Messages captured at the OutboxStaged stage (serialized into outbox entity).
    /// </summary>
    public MessageCollection OutboxStaged => GetCollection(MessageStage.OutboxStaged);

    /// <summary>
    /// Messages captured at the OutboxSent stage (outbox processor sent to transport).
    /// </summary>
    public MessageCollection OutboxSent => GetCollection(MessageStage.OutboxSent);

    /// <summary>
    /// Messages captured at the Received stage (consumer received from transport).
    /// </summary>
    public MessageCollection Received => GetCollection(MessageStage.Received);

    /// <summary>
    /// Messages captured at the Dispatched stage (handler invocation completed).
    /// </summary>
    public MessageCollection Dispatched => GetCollection(MessageStage.Dispatched);

    /// <summary>
    /// Messages captured at the InboxQueued stage (message accepted into inbox).
    /// </summary>
    public MessageCollection InboxQueued => GetCollection(MessageStage.InboxQueued);

    /// <summary>
    /// Messages captured at the InboxDispatched stage (inbox processor attempted handler delivery).
    /// </summary>
    public MessageCollection InboxDispatched => GetCollection(MessageStage.InboxDispatched);

    /// <summary>
    /// Messages captured at the InboxPoisoned stage (handler exceeded max retries).
    /// </summary>
    public MessageCollection InboxPoisoned => GetCollection(MessageStage.InboxPoisoned);

    /// <summary>
    /// Messages captured at the OutboxPoisoned stage (outbox message exceeded max retries).
    /// </summary>
    public MessageCollection OutboxPoisoned => GetCollection(MessageStage.OutboxPoisoned);

    /// <summary>
    /// Waits for a message of the specified type to reach the Published stage.
    /// </summary>
    public Task<TrackedMessage> WaitForPublishedAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.Published, timeout, predicate);

    /// <summary>
    /// Waits for a message of the specified type to reach the Sent stage.
    /// </summary>
    public Task<TrackedMessage> WaitForSentAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.Sent, timeout, predicate);

    /// <summary>
    /// Waits for a message of the specified type to reach the Received stage.
    /// </summary>
    public Task<TrackedMessage> WaitForReceivedAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.Received, timeout, predicate);

    /// <summary>
    /// Waits for a message of the specified type to reach the Dispatched stage.
    /// </summary>
    public Task<TrackedMessage> WaitForDispatchedAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.Dispatched, timeout, predicate);

    /// <summary>
    /// Waits for a message of the specified type to reach the InboxQueued stage.
    /// </summary>
    public Task<TrackedMessage> WaitForInboxQueuedAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.InboxQueued, timeout, predicate);

    /// <summary>
    /// Waits for a message of the specified type to reach the InboxDispatched stage.
    /// </summary>
    public Task<TrackedMessage> WaitForInboxDispatchedAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.InboxDispatched, timeout, predicate);

    /// <summary>
    /// Waits for a message of the specified type to reach the InboxPoisoned stage.
    /// </summary>
    public Task<TrackedMessage> WaitForInboxPoisonedAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.InboxPoisoned, timeout, predicate);

    /// <summary>
    /// Waits for a message of the specified type to reach the OutboxPoisoned stage.
    /// </summary>
    public Task<TrackedMessage> WaitForOutboxPoisonedAsync<T>(
        TimeSpan? timeout = null,
        Func<TrackedMessage, bool>? predicate = null
    )
        where T : notnull => WaitForStageAsync<T>(MessageStage.OutboxPoisoned, timeout, predicate);

    private async Task<TrackedMessage> WaitForStageAsync<T>(
        MessageStage stage,
        TimeSpan? timeout,
        Func<TrackedMessage, bool>? predicate
    )
        where T : notnull
    {
        var effectiveTimeout = timeout ?? _defaultTimeout;
        var traceId = TraceId;

        TrackedMessage? matched = null;
        var activity = await _tracker
            .WaitForAsync(
                a =>
                {
                    if (
                        a.Stage != stage
                        || MessageTracker.ExtractTraceId(a.Properties.TraceParent) != traceId
                        || !MessageTypeMatcher.Matches<T>(a)
                    )
                    {
                        return false;
                    }

                    var tracked = new TrackedMessage(a);
                    if (predicate != null && !predicate(tracked))
                    {
                        return false;
                    }

                    matched = tracked;
                    return true;
                },
                effectiveTimeout
            )
            .ConfigureAwait(false);

        return matched ?? new TrackedMessage(activity);
    }

    private MessageCollection GetCollection(MessageStage stage)
    {
        if (_collections.TryGetValue(stage, out var cached))
        {
            return cached;
        }

        var traceId = TraceId;
        var collection = new MessageCollection(() =>
            _tracker
                .GetActivities(traceId)
                .Where(a => a.Stage == stage)
                .Select(a => new TrackedMessage(a))
        );

        _collections[stage] = collection;
        return collection;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _activity.Stop();
        _activity.Dispose();
        Activity.Current = _previousActivity;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
