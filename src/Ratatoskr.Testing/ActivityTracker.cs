using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

/// <summary>
/// Action-based API for message tracking. Creates a session, executes an action
/// within the session's trace context, and waits for expected completions.
/// </summary>
public class ActivityTracker
{
    private readonly IServiceProvider _services;
    private readonly MessageTracker _tracker;
    private TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private readonly List<WaitCondition> _waitConditions = new();

    internal ActivityTracker(IServiceProvider services, MessageTracker tracker)
    {
        _services = services;
        _tracker = tracker;
    }

    /// <summary>
    /// Sets the timeout for waiting on message activities.
    /// </summary>
    public ActivityTracker Timeout(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    /// <summary>
    /// Adds a wait condition: wait for a message of type T at the specified stage.
    /// </summary>
    public ActivityTracker WaitForMessage<T>(MessageStage stage = MessageStage.Dispatched)
        where T : notnull
    {
        _waitConditions.Add(new WaitCondition(typeof(T), stage));
        return this;
    }

    /// <summary>
    /// Executes the action within a tracked session and waits for all expected completions.
    /// If no explicit wait conditions were added, returns the session immediately after the action completes.
    /// </summary>
    public async Task<MessageTrackingSession> ExecuteAndWaitAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var conditions = new List<WaitCondition>(_waitConditions);
        _waitConditions.Clear();

        var session = new MessageTrackingSession(_tracker, _timeout);

        try
        {
            await action();

            if (conditions.Count > 0)
            {
                var waitTasks = conditions.Select(wc =>
                    _tracker.WaitForAsync(
                        a =>
                            a.Stage == wc.Stage
                            && MessageTracker.ExtractTraceId(a.Properties.TraceParent)
                                == session.TraceId
                            && MessageTypeMatcher.Matches(a, wc.MessageType),
                        _timeout
                    )
                );

                await Task.WhenAll(waitTasks);
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Publishes a message using IRatatoskr and waits for it to be dispatched.
    /// </summary>
    public async Task<MessageTrackingSession> PublishAndWaitAsync<T>(
        T message,
        MessageProperties? props = null,
        CancellationToken cancellationToken = default
    )
        where T : notnull
    {
        if (_waitConditions.Count > 0)
        {
            throw new InvalidOperationException(
                "PublishAndWaitAsync always waits for MessageStage.Dispatched and ignores WaitForMessage conditions. "
                    + "Use ExecuteAndWaitAsync to apply custom wait conditions, or remove WaitForMessage calls."
            );
        }

        var session = new MessageTrackingSession(_tracker, _timeout);

        try
        {
            var bus = _services.GetRequiredService<IRatatoskr>();
            await bus.PublishDirectAsync(message, props, cancellationToken);

            await _tracker.WaitForAsync(
                a =>
                    a.Stage == MessageStage.Dispatched
                    && MessageTracker.ExtractTraceId(a.Properties.TraceParent) == session.TraceId
                    && MessageTypeMatcher.Matches<T>(a),
                _timeout,
                cancellationToken
            );

            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private record WaitCondition(Type MessageType, MessageStage Stage);
}
