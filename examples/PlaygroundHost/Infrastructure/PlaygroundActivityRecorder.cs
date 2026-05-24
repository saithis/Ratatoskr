using System.Collections.Concurrent;
using PlaygroundHost.Persistence.Entities;
using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

public sealed class PlaygroundActivityRecorder : IMessageActivityObserver
{
    private const int MaxEntries = 2500;
    private readonly ConcurrentQueue<PlaygroundActivityEntry> _entries = new();

    public ValueTask OnMessageActivityAsync(MessageActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var orderId = TryResolveOrderId(activity);
        var scenarioRunId = TryResolveScenarioRunId(activity);
        var entry = new PlaygroundActivityEntry(
            activity.Timestamp,
            activity.Stage.ToString(),
            activity.Properties.Id,
            activity.Properties.Type,
            orderId,
            scenarioRunId,
            activity.IsSuccess,
            activity.Exception?.Message,
            activity.TransportName,
            activity.DispatchResult?.ToString()
        );
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }

        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<PlaygroundActivityEntry> GetEntriesForScenarioRun(string scenarioRunId) =>
        _entries.Where(e => e.ScenarioRunId == scenarioRunId).OrderBy(e => e.Timestamp).ToList();

    /// <summary>Backward-compatible filter: order id string matches <see cref="Order.Id"/>.</summary>
    public IReadOnlyList<PlaygroundActivityEntry> GetEntriesForOrder(Guid orderId)
    {
        var key = orderId.ToString("D");
        return _entries.Where(e => e.OrderId == key).OrderBy(e => e.Timestamp).ToList();
    }

    public IReadOnlyList<PlaygroundActivityEntry> GetRecentEntries(int max = 500) =>
        _entries.OrderByDescending(e => e.Timestamp).Take(max).OrderBy(e => e.Timestamp).ToList();

    private static string? TryResolveScenarioRunId(MessageActivity activity)
    {
        if (
            activity.Properties.CloudEventExtensions.TryGetValue(
                PlaygroundCorrelation.CloudEventsExtensionKey,
                out var ext
            )
        )
        {
            if (ext is string s && s.Length > 0)
            {
                return s;
            }
            if (
                ext is System.Text.Json.JsonElement je
                && je.ValueKind == System.Text.Json.JsonValueKind.String
            )
            {
                return je.GetString();
            }
        }

        return TryFromMessageScenarioRunId(activity.Message);
    }

    private static string? TryFromMessageScenarioRunId(object? message) =>
        message is IPlaygroundCorrelatedOrderMessage c && !string.IsNullOrEmpty(c.ScenarioRunId)
            ? c.ScenarioRunId
            : null;

    private static string? TryResolveOrderId(MessageActivity activity)
    {
        var fromMessage = TryFromMessageOrderId(activity.Message);
        if (!string.IsNullOrEmpty(fromMessage) && Guid.TryParse(fromMessage, out _))
        {
            return Guid.Parse(fromMessage).ToString("D");
        }

        if (
            activity.Properties.Id != null
            && PlaygroundMessageIds.TryParseOrderId(activity.Properties.Id, out var id)
        )
        {
            return id.ToString("D");
        }

        return null;
    }

    private static string? TryFromMessageOrderId(object? message) =>
        message is IPlaygroundCorrelatedOrderMessage c ? c.OrderId : null;
}
