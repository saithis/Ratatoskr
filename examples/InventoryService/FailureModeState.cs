namespace InventoryService;

public sealed class FailureModeState
{
    // volatile prevents register-caching across concurrent handler threads (see PR #66)
    private volatile bool _enabled;

    public bool IsEnabled => _enabled;

    public bool Toggle()
    {
        _enabled = !_enabled;
        return _enabled;
    }
}
