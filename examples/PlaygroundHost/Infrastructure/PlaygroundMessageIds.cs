namespace PlaygroundHost.Infrastructure;

/// <summary>Stable CloudEvents ids for playground messages so replay and inbox deduplication are deterministic.</summary>
public static class PlaygroundMessageIds
{
    public static string OrderPlaced(Guid orderId) => $"order-{orderId:D}-placed";

    public static string ProcessOrderCommand(Guid orderId) => $"order-{orderId:D}-cmd";

    public static string OrderFulfilled(Guid orderId) => $"order-{orderId:D}-fulfilled";

    public static string OrderFailed(Guid orderId) => $"order-{orderId:D}-failed";

    public static string ReserveStockInternal(Guid orderId) => $"order-{orderId:D}-reserve";

    public static bool TryParseOrderId(string? messageId, out Guid orderId)
    {
        orderId = default;
        if (string.IsNullOrEmpty(messageId))
        {
            return false;
        }

        const string prefix = "order-";
        if (!messageId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = messageId.AsSpan(prefix.Length);
        var dash = rest.LastIndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        var guidPart = rest[..dash];
        return Guid.TryParse(guidPart, out orderId);
    }
}
