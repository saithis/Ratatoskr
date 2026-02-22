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
    public ActivityTracker WaitForMessage<T>(MessageStage stage = MessageStage.Dispatched) where T : notnull
    {
        _waitConditions.Add(new WaitCondition(typeof(T), stage));
        return this;
    }

    /// <summary>
    /// Executes the action within a tracked session and waits for all expected completions.
    /// If no explicit wait conditions were added, waits for all published messages to be dispatched.
    /// </summary>
    public async Task<MessageTrackingSession> ExecuteAndWaitAsync(Func<Task> action)
    {
        var session = new MessageTrackingSession(_tracker, _timeout);

        await action();

        // If explicit wait conditions were provided, wait for those
        if (_waitConditions.Count > 0)
        {
            var waitTasks = _waitConditions.Select(wc =>
                _tracker.WaitForAsync(
                    a => a.Stage == wc.Stage
                         && MessageTracker.ExtractTraceId(a.Properties.TraceParent) == session.TraceId
                         && MatchesType(a, wc.MessageType),
                    _timeout));

            await Task.WhenAll(waitTasks);
        }

        return session;
    }

    /// <summary>
    /// Publishes a message using IRatatoskr and waits for it to be dispatched.
    /// </summary>
    public async Task<MessageTrackingSession> PublishAndWaitAsync<T>(
        T message,
        MessageProperties? props = null) where T : notnull
    {
        var session = new MessageTrackingSession(_tracker, _timeout);

        var bus = _services.GetRequiredService<IRatatoskr>();
        await bus.PublishDirectAsync(message, props);

        await _tracker.WaitForAsync(
            a => a.Stage == MessageStage.Dispatched
                 && MessageTracker.ExtractTraceId(a.Properties.TraceParent) == session.TraceId
                 && MatchesType(a, typeof(T)),
            _timeout);

        return session;
    }

    private static bool MatchesType(MessageActivity activity, Type expectedType)
    {
        if (activity.MessageType == expectedType)
            return true;

        if (activity.Message?.GetType() == expectedType)
            return true;

        // Match by wire type name from attribute
        var attr = expectedType.GetCustomAttributes(typeof(RatatoskrMessageAttribute), false)
            .FirstOrDefault() as RatatoskrMessageAttribute;
        if (attr?.Type != null && activity.Properties.Type == attr.Type)
            return true;

        return false;
    }

    private record WaitCondition(Type MessageType, MessageStage Stage);
}
