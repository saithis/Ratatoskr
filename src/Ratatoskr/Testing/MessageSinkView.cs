using System.Collections.Concurrent;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// A filtered, session-scoped view of a <see cref="MessageSink"/>.
/// Only includes messages that belong to a specific test session.
/// Provides the same assertion surface as <see cref="MessageSink"/>.
/// </summary>
public class MessageSinkView
{
    private readonly ConcurrentBag<SentMessage> _messages = new();
    private readonly List<(Func<SentMessage, bool> Predicate, TaskCompletionSource<SentMessage> Tcs)> _waiters = new();
    private readonly object _waiterLock = new();

    internal MessageSinkView(MessageSink sink, string sessionId)
    {
        SessionId = sessionId;
        Registry = sink.Registry;

        // Seed with existing messages that match
        foreach (var msg in sink.Messages)
        {
            if (BelongsToSession(msg))
            {
                _messages.Add(msg);
            }
        }

        // Subscribe to future messages
        sink.MessageCaptured += OnMessageCaptured;
    }

    /// <summary>
    /// The session ID this view filters by.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the message type registry used for type matching in assertions.
    /// </summary>
    public ChannelRegistry? Registry { get; }

    /// <summary>
    /// All messages captured in this session.
    /// </summary>
    public IReadOnlyCollection<SentMessage> Messages => _messages.ToArray();

    /// <summary>
    /// Gets the number of messages captured in this session.
    /// </summary>
    public int Count => _messages.Count;

    /// <summary>
    /// Waits for a message matching the predicate to appear in this session.
    /// Checks existing messages first to avoid race conditions.
    /// </summary>
    public async Task<SentMessage> WaitForAsync(
        Func<SentMessage, bool>? predicate = null,
        TimeSpan? timeout = null)
    {
        predicate ??= _ => true;

        // 1. Check existing messages
        var existing = _messages.FirstOrDefault(predicate);
        if (existing != null)
        {
            return existing;
        }

        // 2. Setup waiter
        var tcs = new TaskCompletionSource<SentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_waiterLock)
        {
            // Double-check inside lock
            existing = _messages.FirstOrDefault(predicate);
            if (existing != null) return existing;

            _waiters.Add((predicate, tcs));
        }

        // 3. Wait with timeout
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var timeoutTask = Task.Delay(actualTimeout);
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            lock (_waiterLock)
            {
                _waiters.RemoveAll(x => x.Tcs == tcs);
            }

            throw new TimeoutException($"Timed out waiting for message after {actualTimeout.TotalSeconds}s");
        }

        return await tcs.Task;
    }

    private void OnMessageCaptured(SentMessage message)
    {
        if (!BelongsToSession(message)) return;

        _messages.Add(message);
        NotifyWaiters(message);
    }

    private void NotifyWaiters(SentMessage message)
    {
        lock (_waiterLock)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                var (predicate, tcs) = _waiters[i];
                if (predicate(message))
                {
                    Task.Run(() => tcs.TrySetResult(message));
                    _waiters.RemoveAt(i);
                }
            }
        }
    }

    private bool BelongsToSession(SentMessage message) =>
        message.Properties.Headers.TryGetValue(TestSessionContext.SessionHeaderName, out var sid)
        && sid == SessionId;

    internal void CancelWaiters()
    {
        lock (_waiterLock)
        {
            foreach (var waiter in _waiters)
            {
                waiter.Tcs.TrySetCanceled();
            }

            _waiters.Clear();
        }
    }
}
