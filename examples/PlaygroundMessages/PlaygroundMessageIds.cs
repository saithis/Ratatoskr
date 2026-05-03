namespace PlaygroundMessages;

/// <summary>
/// Stable CloudEvents ids for playground messages so replay and inbox deduplication are deterministic.
/// </summary>
public static class PlaygroundMessageIds
{
    public static string OrderPlaced(Guid orderId) => $"order-{orderId:D}-placed";

    public static string ProcessOrderCommand(Guid orderId) => $"order-{orderId:D}-cmd";
}
