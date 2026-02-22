using System.Collections.Concurrent;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Singleton service that collects all message activities from the pipeline.
/// Thread-safe and supports push-based waiting via registered waiters.
/// </summary>
public class MessageTracker : IMessageActivityObserver
{
    private readonly ConcurrentBag<MessageActivity> _activities = new();
    private readonly ConcurrentBag<Waiter> _waiters = new();

    public ValueTask OnMessageActivity(MessageActivity activity)
    {
        _activities.Add(activity);
        NotifyWaiters(activity);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Gets all recorded activities, optionally filtered by trace ID.
    /// </summary>
    public IReadOnlyList<MessageActivity> GetActivities(string? traceId = null)
    {
        if (traceId == null)
            return _activities.ToArray();

        return _activities
            .Where(a => ExtractTraceId(a.Properties.TraceParent) == traceId)
            .ToList();
    }

    /// <summary>
    /// Waits for a message activity matching the given predicate.
    /// Checks existing activities first, then subscribes to new ones.
    /// </summary>
    public Task<MessageActivity> WaitForAsync(
        Func<MessageActivity, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // Check existing activities first
        var existing = _activities.FirstOrDefault(predicate);
        if (existing != null)
            return Task.FromResult(existing);

        var tcs = new TaskCompletionSource<MessageActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new Waiter { Predicate = predicate, Completion = tcs };
        _waiters.Add(waiter);

        // Handle timeout
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        cts.Token.Register(() => tcs.TrySetException(
            new TimeoutException($"Timed out after {timeout.TotalSeconds}s waiting for message activity.")));

        return tcs.Task;
    }

    /// <summary>
    /// Clears all recorded activities. Useful for test cleanup.
    /// </summary>
    public void Clear()
    {
        _activities.Clear();
    }

    internal static string? ExtractTraceId(string? traceParent)
    {
        if (string.IsNullOrEmpty(traceParent))
            return null;

        // W3C traceparent format: {version}-{trace-id}-{parent-id}-{trace-flags}
        var parts = traceParent.Split('-');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private void NotifyWaiters(MessageActivity activity)
    {
        foreach (var waiter in _waiters)
        {
            if (waiter.Predicate(activity))
            {
                waiter.Completion.TrySetResult(activity);
            }
        }
    }

    private class Waiter
    {
        public required Func<MessageActivity, bool> Predicate { get; init; }
        public required TaskCompletionSource<MessageActivity> Completion { get; init; }
    }
}
