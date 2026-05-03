namespace InventoryService;

/// <summary>
/// Dev-only: normal, throw (inbox retries / poison), succeed-after-N (retry then fulfill), reject (publish OrderFailed).
/// </summary>
public sealed class InventoryDemoModeState
{
    // volatile prevents register-caching across concurrent handler threads
    private volatile int _mode;
    private volatile int _succeedAfterFailuresRemaining;
    private volatile int _succeedAfterInitialBudget;

    public InventoryDemoMode Mode => (InventoryDemoMode)(_mode % 4);

    /// <summary>Cycles Off → Throw → SucceedAfter(2) → Reject → Off.</summary>
    public InventoryDemoMode Cycle()
    {
        var next = ((int)Mode + 1) % 4;
        _mode = next;
        if (Mode == InventoryDemoMode.SucceedAfter)
            SetSucceedAfterBudget(2);
        else
            ClearSucceedAfterBudget();

        return Mode;
    }

    public void SetMode(InventoryDemoMode mode)
    {
        _mode = (int)mode;
        if (mode != InventoryDemoMode.SucceedAfter)
            ClearSucceedAfterBudget();
    }

    public void ApplyFromToggle(string? mode, int? failureCount)
    {
        if (string.IsNullOrEmpty(mode))
        {
            Cycle();
            return;
        }

        var n = failureCount is > 0 ? failureCount.Value : 2;
        switch (mode.ToLowerInvariant())
        {
            case "off":
            case "succeed":
                SetMode(InventoryDemoMode.Off);
                break;
            case "throw":
            case "fail":
                SetMode(InventoryDemoMode.Throw);
                break;
            case "succeed-after":
                _mode = (int)InventoryDemoMode.SucceedAfter;
                SetSucceedAfterBudget(n);
                break;
            case "reject":
                SetMode(InventoryDemoMode.Reject);
                break;
            default:
                throw new InvalidOperationException($"Unknown inventory mode '{mode}'.");
        }
    }

    private void SetSucceedAfterBudget(int n)
    {
        _succeedAfterInitialBudget = n;
        _succeedAfterFailuresRemaining = n;
    }

    private void ClearSucceedAfterBudget()
    {
        _succeedAfterFailuresRemaining = 0;
        _succeedAfterInitialBudget = 0;
    }

    /// <summary>True if ProcessOrderCommand handler should throw this invocation (Throw or SucceedAfter with budget left).</summary>
    public bool TryConsumeProcessFailure()
    {
        if (Mode == InventoryDemoMode.Throw)
            return true;
        if (Mode != InventoryDemoMode.SucceedAfter)
            return false;

        var remaining = _succeedAfterFailuresRemaining;
        if (remaining <= 0)
            return false;

        Interlocked.Decrement(ref _succeedAfterFailuresRemaining);
        return true;
    }

    public int SucceedAfterFailuresRemaining => _succeedAfterFailuresRemaining;

    public int SucceedAfterInitialBudget => _succeedAfterInitialBudget;
}
