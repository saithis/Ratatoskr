namespace PlaygroundHost.Infrastructure;

/// <summary>Dev-only toggles for inbox handler outcomes on the publisher database.</summary>
public sealed class OrderConsumePlaygroundState
{
    private readonly Lock _lock = new();
    private HandlerState _orderFulfilled = new();
    private HandlerState _orderFailed = new();

    private sealed class HandlerState
    {
        public PlaygroundOutcomeMode Mode { get; set; }
        public int FailuresRemaining { get; set; }
    }

    public bool TryConsumeOrderFulfilledFailure()
    {
        lock (_lock) return TryConsumeFailure(_orderFulfilled);
    }

    public bool TryConsumeOrderFailedFailure()
    {
        lock (_lock) return TryConsumeFailure(_orderFailed);
    }

    private static bool TryConsumeFailure(HandlerState s)
    {
        switch (s.Mode)
        {
            case PlaygroundOutcomeMode.Succeed:
                return false;
            case PlaygroundOutcomeMode.AlwaysFail:
                return true;
            case PlaygroundOutcomeMode.SucceedAfterNFailures:
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

    public string CycleOrderFulfilled()
    {
        lock (_lock)
        {
            _orderFulfilled = CycleHandler(_orderFulfilled);
            return Describe(_orderFulfilled);
        }
    }

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
            PlaygroundOutcomeMode.Succeed => ("succeed", 0),
            PlaygroundOutcomeMode.AlwaysFail => ("fail", 0),
            PlaygroundOutcomeMode.SucceedAfterNFailures => ("succeed-after", s.FailuresRemaining),
            _ => ("succeed", 0),
        };

    private static HandlerState CycleHandler(HandlerState current) =>
        current.Mode switch
        {
            PlaygroundOutcomeMode.Succeed => new HandlerState
            {
                Mode = PlaygroundOutcomeMode.AlwaysFail,
                FailuresRemaining = 0,
            },
            PlaygroundOutcomeMode.AlwaysFail => new HandlerState
            {
                Mode = PlaygroundOutcomeMode.SucceedAfterNFailures,
                FailuresRemaining = 2,
            },
            PlaygroundOutcomeMode.SucceedAfterNFailures => new HandlerState
            {
                Mode = PlaygroundOutcomeMode.Succeed,
                FailuresRemaining = 0,
            },
            _ => new HandlerState { Mode = PlaygroundOutcomeMode.Succeed, FailuresRemaining = 0 },
        };

    private static void ApplyMode(HandlerState s, string? mode, int? failureCount)
    {
        var n = failureCount is > 0 ? failureCount.Value : 2;
        switch (mode?.ToLowerInvariant())
        {
            case "succeed":
            case "off":
                s.Mode = PlaygroundOutcomeMode.Succeed;
                s.FailuresRemaining = 0;
                break;
            case "fail":
            case "always-fail":
            case "throw":
                s.Mode = PlaygroundOutcomeMode.AlwaysFail;
                s.FailuresRemaining = 0;
                break;
            case "succeed-after":
                s.Mode = PlaygroundOutcomeMode.SucceedAfterNFailures;
                s.FailuresRemaining = n;
                break;
            default:
                throw new InvalidOperationException($"Unknown mode '{mode}'. Use succeed, fail, or succeed-after.");
        }
    }

    private static string Describe(HandlerState s) =>
        s.Mode switch
        {
            PlaygroundOutcomeMode.Succeed => "succeed",
            PlaygroundOutcomeMode.AlwaysFail => "fail",
            PlaygroundOutcomeMode.SucceedAfterNFailures =>
                $"succeed-after({s.FailuresRemaining} remaining of initial budget)",
            _ => "succeed",
        };
}
