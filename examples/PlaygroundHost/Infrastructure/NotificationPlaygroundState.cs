namespace PlaygroundHost.Infrastructure;

/// <summary>Dev-only per-handler toggles for Rabbit inline consumers (no inbox).</summary>
public sealed class NotificationPlaygroundState
{
    private readonly Lock _lock = new();
    private HandlerState _orderPlacedNotify = new();
    private HandlerState _orderPlacedAnalytics = new();
    private HandlerState _orderFulfilledNotify = new();

    private sealed class HandlerState
    {
        public PlaygroundOutcomeMode Mode { get; set; }
        public int FailuresRemaining { get; set; }
    }

    public (string Mode, int FailuresRemaining) GetOrderPlacedNotifyApi()
    {
        lock (_lock) return ToApi(_orderPlacedNotify);
    }

    public (string Mode, int FailuresRemaining) GetOrderPlacedAnalyticsApi()
    {
        lock (_lock) return ToApi(_orderPlacedAnalytics);
    }

    public (string Mode, int FailuresRemaining) GetOrderFulfilledNotifyApi()
    {
        lock (_lock) return ToApi(_orderFulfilledNotify);
    }

    public bool TryConsumeOrderPlacedNotifyFailure()
    {
        lock (_lock) return TryConsumeFailure(_orderPlacedNotify);
    }

    public bool TryConsumeOrderPlacedAnalyticsFailure()
    {
        lock (_lock) return TryConsumeFailure(_orderPlacedAnalytics);
    }

    public bool TryConsumeOrderFulfilledNotifyFailure()
    {
        lock (_lock) return TryConsumeFailure(_orderFulfilledNotify);
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

    public string CycleOrderPlacedNotify()
    {
        lock (_lock)
        {
            _orderPlacedNotify = CycleHandler(_orderPlacedNotify);
            return DescribeApi(_orderPlacedNotify).Mode;
        }
    }

    public string CycleOrderPlacedAnalytics()
    {
        lock (_lock)
        {
            _orderPlacedAnalytics = CycleHandler(_orderPlacedAnalytics);
            return DescribeApi(_orderPlacedAnalytics).Mode;
        }
    }

    public string CycleOrderFulfilledNotify()
    {
        lock (_lock)
        {
            _orderFulfilledNotify = CycleHandler(_orderFulfilledNotify);
            return DescribeApi(_orderFulfilledNotify).Mode;
        }
    }

    public string ApplyOrderPlacedNotify(string? mode, int? failureCount)
    {
        lock (_lock)
        {
            ApplyMode(_orderPlacedNotify, mode, failureCount);
            return DescribeApi(_orderPlacedNotify).Mode;
        }
    }

    public string ApplyOrderPlacedAnalytics(string? mode, int? failureCount)
    {
        lock (_lock)
        {
            ApplyMode(_orderPlacedAnalytics, mode, failureCount);
            return DescribeApi(_orderPlacedAnalytics).Mode;
        }
    }

    public string ApplyOrderFulfilledNotify(string? mode, int? failureCount)
    {
        lock (_lock)
        {
            ApplyMode(_orderFulfilledNotify, mode, failureCount);
            return DescribeApi(_orderFulfilledNotify).Mode;
        }
    }

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
                s.Mode = PlaygroundOutcomeMode.AlwaysFail;
                s.FailuresRemaining = 0;
                break;
            case "succeed-after":
                s.Mode = PlaygroundOutcomeMode.SucceedAfterNFailures;
                s.FailuresRemaining = n;
                break;
            default:
                throw new InvalidOperationException($"Unknown mode '{mode}'.");
        }
    }

    private static (string Mode, int FailuresRemaining) ToApi(HandlerState s) => DescribeApi(s);

    private static (string Mode, int FailuresRemaining) DescribeApi(HandlerState s) =>
        s.Mode switch
        {
            PlaygroundOutcomeMode.Succeed => ("succeed", 0),
            PlaygroundOutcomeMode.AlwaysFail => ("fail", 0),
            PlaygroundOutcomeMode.SucceedAfterNFailures => ("succeed-after", s.FailuresRemaining),
            _ => ("succeed", 0),
        };
}
