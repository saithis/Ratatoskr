using Ratatoskr;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

/// <summary>
/// Playground catalog scenario that owns its Ratatoskr channel wiring via <see cref="RegisterRatatoskrTopology"/>.
/// </summary>
public interface IPlaygroundScenario : IScenario
{
    public static abstract void RegisterRatatoskrTopology(RatatoskrBuilder bus);

    /// <summary>Main queues this scenario creates; empty when there is nothing to probe.</summary>
    public static abstract IReadOnlyList<PlaygroundRabbitQueue> RabbitQueues { get; }
}
