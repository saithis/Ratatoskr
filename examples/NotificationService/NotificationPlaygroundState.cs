namespace NotificationService;

/// <summary>Dev-only per-handler toggles for Rabbit inline consumers (no inbox).</summary>
public sealed class NotificationPlaygroundState
{
    private volatile bool _orderPlacedHandlerFails;
    private volatile bool _orderFulfilledHandlerFails;

    public bool OrderPlacedHandlerFails => _orderPlacedHandlerFails;

    public bool OrderFulfilledHandlerFails => _orderFulfilledHandlerFails;

    public bool ToggleOrderPlacedHandlerFails()
    {
        _orderPlacedHandlerFails = !_orderPlacedHandlerFails;
        return _orderPlacedHandlerFails;
    }

    public bool ToggleOrderFulfilledHandlerFails()
    {
        _orderFulfilledHandlerFails = !_orderFulfilledHandlerFails;
        return _orderFulfilledHandlerFails;
    }
}
