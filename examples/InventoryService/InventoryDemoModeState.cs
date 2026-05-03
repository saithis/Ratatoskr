namespace InventoryService;

/// <summary>
/// Dev-only tri-state: normal, throw (inbox retries / poison), reject (publish OrderFailed).
/// </summary>
public sealed class InventoryDemoModeState
{
    // volatile prevents register-caching across concurrent handler threads
    private volatile int _mode;

    public InventoryDemoMode Mode => (InventoryDemoMode)(_mode % 3);

    /// <summary>Cycles Off → Throw → Reject → Off.</summary>
    public InventoryDemoMode Cycle()
    {
        var next = ((int)Mode + 1) % 3;
        _mode = next;
        return Mode;
    }

    public void SetMode(InventoryDemoMode mode) => _mode = (int)mode;
}
