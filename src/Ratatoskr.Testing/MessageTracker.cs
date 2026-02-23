using System.Collections.Concurrent;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Singleton service that collects all message activities from the pipeline.
/// Thread-safe and supports push-based waiting via registered waiters.
/// </summary>
public class MessageTracker : IMessageActivityObserver
{
    private readonly ConcurrentQueue<MessageActivity> _activities = new();
    private readonly List<Waiter> _waiters = new();
    private readonly object _lock = new();

    public ValueTask OnMessageActivity(MessageActivity activity)
    {
        _activities.Enqueue(activity);
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
    /// Atomically checks existing activities and subscribes for new ones to avoid TOCTOU races.
    /// </summary>
    public Task<MessageActivity> WaitForAsync(
        Func<MessageActivity, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Check existing activities while holding the lock
            var existing = _activities.FirstOrDefault(predicate);
            if (existing != null)
                return Task.FromResult(existing);

            // Register waiter while still holding the lock — no activity can slip through
            var tcs = new TaskCompletionSource<MessageActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            cts.Token.Register(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetCanceled(cancellationToken);
                else
                    tcs.TrySetException(
                        new TimeoutException($"Timed out after {timeout.TotalSeconds}s waiting for message activity."));
                cts.Dispose();
            });

            _waiters.Add(new Waiter(predicate, tcs, cts));
            return tcs.Task;
        }
    }

    /// <summary>
    /// Clears all recorded activities and waiters. Useful for test cleanup.
    /// </summary>
    public void Clear()
    {
        _activities.Clear();
        lock (_lock)
        {
            foreach (var waiter in _waiters)
            {
                waiter.Completion.TrySetCanceled();
                waiter.Cts.Dispose();
            }

            _waiters.Clear();
        }
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
        lock (_lock)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];

                if (waiter.Completion.Task.IsCompleted)
                {
                    // Remove already completed (timed-out) waiters
                    _waiters.RemoveAt(i);
                    continue;
                }

                try
                {
                    if (waiter.Predicate(activity))
                    {
                        waiter.Completion.TrySetResult(activity);
                        waiter.Cts.Dispose();
                        _waiters.RemoveAt(i);
                    }
                }
                catch
                {
                    // Predicate failures must not prevent other waiters from being notified
                }
            }
        }
    }

    private record Waiter(
        Func<MessageActivity, bool> Predicate,
        TaskCompletionSource<MessageActivity> Completion,
        CancellationTokenSource Cts);
}
