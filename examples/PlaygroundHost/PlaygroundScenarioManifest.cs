using Microsoft.Extensions.DependencyInjection;
using PlaygroundHost.Infrastructure.ScenarioRunning;
using PlaygroundHost.Scenarios.DirectConsume.DirectConsumeDlq;
using PlaygroundHost.Scenarios.DirectConsume.DirectConsumeRetry;
using PlaygroundHost.Scenarios.DirectConsume.DirectConsumeSuccess;
using PlaygroundHost.Scenarios.Inbox.BusinessRejection;
using PlaygroundHost.Scenarios.Inbox.InboxPoison;
using PlaygroundHost.Scenarios.Inbox.InboxRetryThenSuccess;
using PlaygroundHost.Scenarios.Other.EfcoreInternalCommand;
using PlaygroundHost.Scenarios.Other.FanoutTwoHandlersOnOrderplaced;
using PlaygroundHost.Scenarios.Other.ReplayDedups;
using PlaygroundHost.Scenarios.Outbox.OutboxPoison;
using PlaygroundHost.Scenarios.Outbox.OutboxRetryThenSuccess;
using PlaygroundHost.Scenarios.Outbox.OutboxSuccess;
using PlaygroundHost.Scenarios.Outbox.OversizedPayloadRollsBack;
using PlaygroundHost.Scenarios.Tests.BlockingHold;
using PlaygroundHost.Scenarios.Tests.CancelSmoke;
using Ratatoskr;

namespace PlaygroundHost;

/// <summary>Single list of all playground scenario types for DI, Ratatoskr topology registration, and rabbit-depth probes.</summary>
internal static class PlaygroundScenarioManifest
{
    private sealed record ScenarioEntry(
        IScenario Scenario,
        Action<RatatoskrBuilder> RegisterTopology,
        IReadOnlyList<PlaygroundRabbitDepthQueue> DepthQueues);

    private static ScenarioEntry Entry<T>() where T : IPlaygroundScenario, new() =>
        new(new T(), T.RegisterRatatoskrTopology, T.RabbitDepthQueues);

    private static readonly ScenarioEntry[] _all =
    [
        Entry<OutboxSuccessScenario>(),
        Entry<OutboxRetryThenSuccessScenario>(),
        Entry<OutboxPoisonScenario>(),
        Entry<OversizedPayloadRollsBackScenario>(),
        Entry<InboxRetryThenSuccessScenario>(),
        Entry<InboxPoisonScenario>(),
        Entry<BusinessRejectionScenario>(),
        Entry<DirectConsumeSuccessScenario>(),
        Entry<DirectConsumeRetryScenario>(),
        Entry<DirectConsumeDlqScenario>(),
        Entry<FanoutTwoHandlersOnOrderplacedScenario>(),
        Entry<EfcoreInternalCommandScenario>(),
        Entry<ReplayDedupsScenario>(),
        Entry<BlockingHoldScenario>(),
        Entry<CancelSmokeScenario>(),
    ];

    internal static void RegisterScenarioServices(IServiceCollection services)
    {
        foreach (var e in _all)
            services.AddSingleton(typeof(IScenario), e.Scenario.GetType());
    }

    internal static void RegisterScenarioTopologies(RatatoskrBuilder bus)
    {
        foreach (var e in _all)
            e.RegisterTopology(bus);
    }

    internal static IEnumerable<(string Slug, PlaygroundRabbitDepthQueue Queue)> EnumerateRabbitDepthProbeTargets() =>
        from e in _all
        where e.DepthQueues.Count > 0
        from q in e.DepthQueues
        select (e.Scenario.Slug, q);
}
