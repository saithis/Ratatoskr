using System.Collections.Concurrent;
using PlaygroundMessages.Messages;
using Ratatoskr.Core;

namespace PlaygroundMessages;

public sealed record PlaygroundActivityEntry(
    DateTimeOffset Timestamp,
    string Stage,
    string? MessageId,
    string? MessageType,
    string? OrderId,
    bool? IsSuccess,
    string? Error,
    string? TransportName,
    string? DispatchResult);

/// <summary>
/// Dev-only in-memory capture of <see cref="MessageStage"/> events for the playground dashboard.
/// </summary>
public sealed class PlaygroundActivityRecorder : IMessageActivityObserver
{
    private const int MaxEntries = 2500;
    private readonly ConcurrentQueue<PlaygroundActivityEntry> _entries = new();

    public ValueTask OnMessageActivity(MessageActivity activity)
    {
        var orderId = TryResolveOrderId(activity);
        var entry = new PlaygroundActivityEntry(
            activity.Timestamp,
            activity.Stage.ToString(),
            activity.Properties.Id,
            activity.Properties.Type,
            orderId,
            activity.IsSuccess,
            activity.Exception?.Message,
            activity.TransportName,
            activity.DispatchResult?.ToString());
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }

        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<PlaygroundActivityEntry> GetEntriesForOrder(Guid orderId)
    {
        var key = orderId.ToString("D");
        return _entries
            .Where(e => e.OrderId == key)
            .OrderBy(e => e.Timestamp)
            .ToList();
    }

    public IReadOnlyList<PlaygroundActivityEntry> GetRecentEntries(int max = 500) =>
        _entries.OrderByDescending(e => e.Timestamp).Take(max).OrderBy(e => e.Timestamp).ToList();

    private static string? TryResolveOrderId(MessageActivity activity)
    {
        var fromMessage = TryFromMessageOrderId(activity.Message);
        if (!string.IsNullOrEmpty(fromMessage) && Guid.TryParse(fromMessage, out _))
            return Guid.Parse(fromMessage).ToString("D");

        if (activity.Properties.Id != null && PlaygroundMessageIds.TryParseOrderId(activity.Properties.Id, out var id))
            return id.ToString("D");

        return null;
    }

    private static string? TryFromMessageOrderId(object? message) =>
        message switch
        {
            OrderPlaced o => o.OrderId,
            ProcessOrderCommand c => c.OrderId,
            OrderFulfilled f => f.OrderId,
            OrderFailed x => x.OrderId,
            ReserveStockInternal r => r.OrderId,
            _ => null,
        };
}
