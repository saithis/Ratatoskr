namespace PlaygroundHost.Infrastructure.ScenarioRunning;

/// <summary>Shared timeouts and poll intervals for playground scenarios.</summary>
public static class ScenarioTiming
{
    public static readonly TimeSpan OrderEventuallyLong = TimeSpan.FromSeconds(18);

    public static readonly TimeSpan OrderEventuallyMedium = TimeSpan.FromSeconds(18);

    public static readonly TimeSpan PollLoopLong = TimeSpan.FromSeconds(18);

    public static readonly TimeSpan OrderPollInterval = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan PollIntervalSlow = TimeSpan.FromMilliseconds(800);

    public static readonly TimeSpan DlqPollInterval = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan ReplaySettleDelay = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan EfCoreActivitySettleDelay = TimeSpan.FromSeconds(4);
}
