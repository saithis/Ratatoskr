namespace PlaygroundMessages;

public static class HandlerKeys
{
    public const string InventoryProcessOrder  = "inventory-process-order";
    public const string OrderFulfilled         = "order-fulfilled";
    public const string OrderFailed            = "order-failed";
    public const string NotifyOrderPlaced      = "notify-order-placed";
    public const string NotifyOrderFulfilled   = "notify-order-fulfilled";
    public const string AnalyticsOrderPlaced   = "analytics-order-placed";

    public const string ReserveStockInternal   = "orderservice-reserve-stock-internal";
}
