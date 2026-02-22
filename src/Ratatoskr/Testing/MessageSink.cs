using System.Collections.Concurrent;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Captures messages sent via Ratatoskr for test assertions and waiting.
/// Acts as the <see cref="IMessageSender"/> implementation in test scenarios,
/// optionally forwarding to a real sender when testing with a real transport.
/// Thread-safe for parallel test execution.
/// </summary>
public class MessageSink : IMessageSender
{
    private readonly ConcurrentBag<SentMessage> _messages = new();
    private readonly List<(Func<SentMessage, bool> Predicate, TaskCompletionSource<SentMessage> Tcs)> _waiters = new();
    private readonly object _waiterLock = new();
    private IMessageSender? _innerSender;

    /// <summary>
    /// Gets the message type registry used for type matching in assertions.
    /// </summary>
    public ChannelRegistry? Registry { get; init; }

    /// <summary>
    /// All messages that have been captured.
    /// </summary>
    public IReadOnlyCollection<SentMessage> Messages => _messages.ToArray();

    /// <summary>
    /// Gets the number of captured messages.
    /// </summary>
    public int Count => _messages.Count;

    /// <summary>
    /// Event triggered when a message is captured.
    /// </summary>
    public event Action<SentMessage>? MessageCaptured;

    /// <summary>
    /// Sets an inner sender that messages will be forwarded to after capture.
    /// Used when testing with a real transport (<c>ReplaceTransport = false</c>).
    /// </summary>
    internal void SetInnerSender(IMessageSender sender) => _innerSender = sender;

    /// <summary>
    /// Captures the message and optionally forwards it to the inner sender.
    /// </summary>
    async Task IMessageSender.SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        var message = new SentMessage(content, props, DateTimeOffset.UtcNow);
        _messages.Add(message);

        MessageCaptured?.Invoke(message);
        NotifyWaiters(message);

        if (_innerSender != null)
        {
            await _innerSender.SendAsync(content, props, cancellationToken);
        }
    }

    /// <summary>
    /// Waits for a message matching the predicate to be captured.
    /// Checks existing messages first to avoid race conditions.
    /// </summary>
    public async Task<SentMessage> WaitForAsync(
        Func<SentMessage, bool>? predicate = null,
        TimeSpan? timeout = null,
        bool checkExisting = true)
    {
        predicate ??= _ => true;

        // 1. Check existing messages first
        if (checkExisting)
        {
            var existing = _messages.FirstOrDefault(predicate);
            if (existing != null)
            {
                return existing;
            }
        }

        // 2. Setup waiter
        var tcs = new TaskCompletionSource<SentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_waiterLock)
        {
            // Double check inside lock in case one arrived just now
            if (checkExisting)
            {
                var existing = _messages.FirstOrDefault(predicate);
                if (existing != null) return existing;
            }

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

    /// <summary>
    /// Clears all captured messages and cancels pending waiters.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
        lock (_waiterLock)
        {
            foreach (var waiter in _waiters)
            {
                waiter.Tcs.TrySetCanceled();
            }

            _waiters.Clear();
        }
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
                    // Move callback to a different thread to avoid blocking the sender
                    Task.Run(() => tcs.TrySetResult(message));
                    _waiters.RemoveAt(i);
                }
            }
        }
    }
}
