using System.Reflection;
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
    internal static readonly Type[] All =
    [
        typeof(OutboxSuccessScenario),
        typeof(OutboxRetryThenSuccessScenario),
        typeof(OutboxPoisonScenario),
        typeof(OversizedPayloadRollsBackScenario),
        typeof(InboxRetryThenSuccessScenario),
        typeof(InboxPoisonScenario),
        typeof(BusinessRejectionScenario),
        typeof(DirectConsumeSuccessScenario),
        typeof(DirectConsumeRetryScenario),
        typeof(DirectConsumeDlqScenario),
        typeof(FanoutTwoHandlersOnOrderplacedScenario),
        typeof(EfcoreInternalCommandScenario),
        typeof(ReplayDedupsScenario),
        typeof(BlockingHoldScenario),
        typeof(CancelSmokeScenario),
    ];

    internal static void RegisterScenarioServices(IServiceCollection services)
    {
        foreach (var t in All)
            services.AddSingleton(typeof(IScenario), t);
    }

    internal static void RegisterScenarioTopologies(RatatoskrBuilder bus)
    {
        foreach (var t in All)
        {
            var m = t.GetMethod(
                nameof(IPlaygroundScenario.RegisterRatatoskrTopology),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(RatatoskrBuilder)],
                modifiers: null);
            if (m is null)
                throw new InvalidOperationException(
                    $"{t.FullName} must implement public static void RegisterRatatoskrTopology(RatatoskrBuilder).");
            m.Invoke(null, [bus]);
        }
    }

    internal static IEnumerable<(string Slug, PlaygroundRabbitDepthQueue Queue)> EnumerateRabbitDepthProbeTargets()
    {
        foreach (var t in All)
        {
            if (!typeof(IPlaygroundScenario).IsAssignableFrom(t))
                continue;
            var prop = t.GetProperty(
                nameof(IPlaygroundScenario.RabbitDepthQueues),
                BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is not IReadOnlyList<PlaygroundRabbitDepthQueue> list || list.Count == 0)
                continue;
            var scenario = (IScenario)Activator.CreateInstance(t)!;
            foreach (var q in list)
                yield return (scenario.Slug, q);
        }
    }
}
