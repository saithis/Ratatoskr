namespace OrderService.Playground;

/// <summary>Dev-only toggles for inbox handler outcomes on OrderService.</summary>
public sealed class OrderConsumePlaygroundState
{
    private readonly Lock _lock = new();
    private HandlerState _orderFulfilled = new();
    private HandlerState _orderFailed = new();

    private sealed class HandlerState
    {
        public PlaygroundMessages.PlaygroundOutcomeMode Mode { get; set; }
        public int FailuresRemaining { get; set; }
    }

    public (PlaygroundMessages.PlaygroundOutcomeMode Mode, int FailuresRemaining) GetOrderFulfilledState()
    {
        lock (_lock) return (_orderFulfilled.Mode, _orderFulfilled.FailuresRemaining);
    }

    public (PlaygroundMessages.PlaygroundOutcomeMode Mode, int FailuresRemaining) GetOrderFailedState()
    {
        lock (_lock) return (_orderFailed.Mode, _orderFailed.FailuresRemaining);
    }

    /// <summary>Returns true if the handler should throw this invocation.</summary>
    public bool TryConsumeOrderFulfilledFailure()
    {
        lock (_lock) return TryConsumeFailure(_orderFulfilled);
    }

    /// <summary>Returns true if the handler should throw this invocation.</summary>
    public bool TryConsumeOrderFailedFailure()
    {
        lock (_lock) return TryConsumeFailure(_orderFailed);
    }

    private static bool TryConsumeFailure(HandlerState s)
    {
        switch (s.Mode)
        {
            case PlaygroundMessages.PlaygroundOutcomeMode.Succeed:
                return false;
            case PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail:
                return true;
            case PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures:
                if (s.FailuresRemaining > 0)
                {
                    s.FailuresRemaining--;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Cycles: succeed → always-fail → succeed-after(2) → succeed.</summary>
    public string CycleOrderFulfilled()
    {
        lock (_lock)
        {
            _orderFulfilled = CycleHandler(_orderFulfilled);
            return Describe(_orderFulfilled);
        }
    }

    /// <summary>Cycles: succeed → always-fail → succeed-after(2) → succeed.</summary>
    public string CycleOrderFailed()
    {
        lock (_lock)
        {
            _orderFailed = CycleHandler(_orderFailed);
            return Describe(_orderFailed);
        }
    }

    public string ApplyOrderFulfilled(string? mode, int? failureCount)
    {
        lock (_lock)
        {
            ApplyMode(_orderFulfilled, mode, failureCount);
            return Describe(_orderFulfilled);
        }
    }

    public string ApplyOrderFailed(string? mode, int? failureCount)
    {
        lock (_lock)
        {
            ApplyMode(_orderFailed, mode, failureCount);
            return Describe(_orderFailed);
        }
    }

    public (string Mode, int FailuresRemaining) GetOrderFulfilledApi()
    {
        lock (_lock) return ToApi(_orderFulfilled);
    }

    public (string Mode, int FailuresRemaining) GetOrderFailedApi()
    {
        lock (_lock) return ToApi(_orderFailed);
    }

    private static (string Mode, int FailuresRemaining) ToApi(HandlerState s) =>
        s.Mode switch
        {
            PlaygroundMessages.PlaygroundOutcomeMode.Succeed => ("succeed", 0),
            PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail => ("fail", 0),
            PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures => ("succeed-after", s.FailuresRemaining),
            _ => ("succeed", 0),
        };

    private static HandlerState CycleHandler(HandlerState current)
    {
        var next = current.Mode switch
        {
            PlaygroundMessages.PlaygroundOutcomeMode.Succeed => new HandlerState
            {
                Mode = PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail,
                FailuresRemaining = 0,
            },
            PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail => new HandlerState
            {
                Mode = PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures,
                FailuresRemaining = 2,
            },
            PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures => new HandlerState
            {
                Mode = PlaygroundMessages.PlaygroundOutcomeMode.Succeed,
                FailuresRemaining = 0,
            },
            _ => new HandlerState { Mode = PlaygroundMessages.PlaygroundOutcomeMode.Succeed, FailuresRemaining = 0 },
        };
        return next;
    }

    private static void ApplyMode(HandlerState s, string? mode, int? failureCount)
    {
        var n = failureCount is > 0 ? failureCount.Value : 2;
        switch (mode?.ToLowerInvariant())
        {
            case "succeed":
            case "off":
                s.Mode = PlaygroundMessages.PlaygroundOutcomeMode.Succeed;
                s.FailuresRemaining = 0;
                break;
            case "fail":
            case "always-fail":
            case "throw":
                s.Mode = PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail;
                s.FailuresRemaining = 0;
                break;
            case "succeed-after":
                s.Mode = PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures;
                s.FailuresRemaining = n;
                break;
            default:
                throw new InvalidOperationException($"Unknown mode '{mode}'. Use succeed, fail, or succeed-after.");
        }
    }

    private static string Describe(HandlerState s) =>
        s.Mode switch
        {
            PlaygroundMessages.PlaygroundOutcomeMode.Succeed => "succeed",
            PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail => "fail",
            PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures =>
                $"succeed-after({s.FailuresRemaining} remaining of initial budget)",
            _ => "succeed",
        };
}
