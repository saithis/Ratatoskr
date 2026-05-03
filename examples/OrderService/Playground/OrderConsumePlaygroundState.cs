namespace OrderService.Playground;

/// <summary>Dev-only toggles for inbox handler outcomes on OrderService.</summary>
public sealed class OrderConsumePlaygroundState
{
    private readonly Lock _lock = new();
    private bool _orderFulfilledHandlerFails;
    private bool _orderFailedHandlerFails;

    public bool OrderFulfilledHandlerFails
    {
        get { lock (_lock) return _orderFulfilledHandlerFails; }
    }

    public bool OrderFailedHandlerFails
    {
        get { lock (_lock) return _orderFailedHandlerFails; }
    }

    public bool ToggleOrderFulfilledFails()
    {
        lock (_lock) return _orderFulfilledHandlerFails = !_orderFulfilledHandlerFails;
    }

    public bool ToggleOrderFailedFails()
    {
        lock (_lock) return _orderFailedHandlerFails = !_orderFailedHandlerFails;
    }
}
