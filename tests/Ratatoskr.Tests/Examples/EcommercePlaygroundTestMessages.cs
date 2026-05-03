// Minimal message shapes for in-process Ratatoskr tests (playground HTTP coverage uses PlaygroundHostAppMarker in Ratatoskr.Tests).
using Ratatoskr;

namespace Ratatoskr.Tests.Examples;

public static class EcommerceHandlerKeys
{
    public const string InventoryProcessOrder = "InventoryProcessOrder";
    public const string ReserveStockInternal = "ReserveStockInternal";
}

/// <summary>Stable CloudEvents ids (same shape as examples playground).</summary>
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
            return false;

        const string prefix = "order-";
        if (!messageId.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rest = messageId.AsSpan(prefix.Length);
        var dash = rest.LastIndexOf('-');
        if (dash <= 0)
            return false;

        var guidPart = rest[..dash];
        return Guid.TryParse(guidPart, out orderId);
    }
}

[RatatoskrMessage("ecommerce.order.placed")]
public class OrderPlaced
{
    public required string OrderId { get; init; }
}

[RatatoskrMessage("ecommerce.inventory.process")]
public class ProcessOrderCommand
{
    public required string OrderId { get; init; }
}

[RatatoskrMessage("ecommerce.order.fulfilled")]
public class OrderFulfilled
{
    public required string OrderId { get; init; }
}

[RatatoskrMessage("ecommerce.order.failed")]
public class OrderFailed
{
    public required string OrderId { get; init; }
    public required string Reason { get; init; }
}

[RatatoskrMessage("orders.internal.reserve-stock")]
public class ReserveStockInternal
{
    public required string OrderId { get; init; }
}
