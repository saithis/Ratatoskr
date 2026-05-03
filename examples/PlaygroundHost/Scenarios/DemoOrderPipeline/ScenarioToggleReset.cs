using PlaygroundHost.Infrastructure;

namespace PlaygroundHost.Scenarios.DemoOrderPipeline;

public static class ScenarioToggleReset
{
    public static void ApplyBaseline(IServiceProvider services)
    {
        services.GetRequiredService<OutboxFailureState>().Apply("succeed", null);
        services.GetRequiredService<OutboxFailureState>().SetActiveScenarioRun(null);

        services.GetRequiredService<OrderConsumePlaygroundState>().ApplyOrderFulfilled("succeed", null);
        services.GetRequiredService<OrderConsumePlaygroundState>().ApplyOrderFailed("succeed", null);

        services.GetRequiredService<InventoryDemoModeState>().SetMode(InventoryDemoMode.Off);

        var notifications = services.GetRequiredService<NotificationPlaygroundState>();
        notifications.ApplyOrderPlacedNotify("succeed", null);
        notifications.ApplyOrderPlacedAnalytics("succeed", null);
        notifications.ApplyOrderFulfilledNotify("succeed", null);
    }
}
