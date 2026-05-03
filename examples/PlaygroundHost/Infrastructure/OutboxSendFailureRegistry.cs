using System.Collections.Concurrent;
using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

/// <summary>Per scenario-run simulated failures for <see cref="IMessageSender"/> (outbox relay + PublishDirect).</summary>
public sealed class OutboxSendFailureRegistry
{
    private readonly ConcurrentDictionary<string, Policy> _byScenarioRun = new(StringComparer.Ordinal);

    public void Register(string scenarioRunId, OutboxSendFailureKind kind, int succeedAfterFailureCount = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioRunId);
        _byScenarioRun[scenarioRunId] = kind switch
        {
            OutboxSendFailureKind.AlwaysFail => new Policy(PlaygroundOutcomeMode.AlwaysFail, 0),
            OutboxSendFailureKind.SucceedAfterNFailures => new Policy(PlaygroundOutcomeMode.SucceedAfterNFailures, succeedAfterFailureCount),
            _ => new Policy(PlaygroundOutcomeMode.Succeed, 0),
        };
    }

    public void Unregister(string scenarioRunId)
    {
        if (!string.IsNullOrEmpty(scenarioRunId))
            _byScenarioRun.TryRemove(scenarioRunId, out _);
    }

    public bool TryConsumeSendFailure(MessageProperties props)
    {
        if (!props.CloudEventExtensions.TryGetValue(PlaygroundCorrelation.CloudEventsExtensionKey, out var ext) ||
            ext is not string runId ||
            string.IsNullOrEmpty(runId))
            return false;

        if (!_byScenarioRun.TryGetValue(runId, out var policy))
            return false;

        return policy.TryConsumeFailure();
    }

    private sealed class Policy
    {
        private readonly Lock _lock = new();
        private PlaygroundOutcomeMode _mode;
        private int _failuresRemaining;

        public Policy(PlaygroundOutcomeMode mode, int failuresRemaining)
        {
            _mode = mode;
            _failuresRemaining = failuresRemaining;
        }

        public bool TryConsumeFailure()
        {
            lock (_lock)
            {
                switch (_mode)
                {
                    case PlaygroundOutcomeMode.Succeed:
                        return false;
                    case PlaygroundOutcomeMode.AlwaysFail:
                        return true;
                    case PlaygroundOutcomeMode.SucceedAfterNFailures:
                        if (_failuresRemaining > 0)
                        {
                            _failuresRemaining--;
                            return true;
                        }

                        return false;
                    default:
                        return false;
                }
            }
        }
    }
}

public enum OutboxSendFailureKind
{
    Succeed,
    AlwaysFail,
    SucceedAfterNFailures,
}
