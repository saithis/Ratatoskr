using System.Collections.Concurrent;
using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

/// <summary>Per scenario-run simulated failures for <see cref="IMessageSender"/> (outbox relay + PublishDirect).</summary>
public sealed class OutboxSendFailureRegistry
{
    private readonly ConcurrentDictionary<string, Policy> _byScenarioRun = new(
        StringComparer.Ordinal
    );

    public void Register(
        string scenarioRunId,
        OutboxSendFailureKind kind,
        int succeedAfterFailureCount = 2
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioRunId);
        _byScenarioRun[scenarioRunId] = kind switch
        {
            OutboxSendFailureKind.AlwaysFail => new Policy(OutboxSendFailureKind.AlwaysFail, 0),
            OutboxSendFailureKind.SucceedAfterNFailures => new Policy(
                OutboxSendFailureKind.SucceedAfterNFailures,
                succeedAfterFailureCount
            ),
            _ => new Policy(OutboxSendFailureKind.Succeed, 0),
        };
    }

    public void Unregister(string scenarioRunId)
    {
        if (!string.IsNullOrEmpty(scenarioRunId))
            _byScenarioRun.TryRemove(scenarioRunId, out _);
    }

    public bool TryConsumeSendFailure(MessageProperties props)
    {
        if (
            !props.CloudEventExtensions.TryGetValue(
                PlaygroundCorrelation.CloudEventsExtensionKey,
                out var ext
            )
        )
            return false;

        string? runId = ext as string;
        if (
            runId == null
            && ext is System.Text.Json.JsonElement json
            && json.ValueKind == System.Text.Json.JsonValueKind.String
        )
        {
            runId = json.GetString();
        }

        if (string.IsNullOrEmpty(runId))
            return false;

        if (!_byScenarioRun.TryGetValue(runId, out var policy))
            return false;

        return policy.TryConsumeFailure();
    }

    private sealed class Policy
    {
        private readonly Lock _lock = new();
        private OutboxSendFailureKind _mode;
        private int _failuresRemaining;

        public Policy(OutboxSendFailureKind mode, int failuresRemaining)
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
                    case OutboxSendFailureKind.Succeed:
                        return false;
                    case OutboxSendFailureKind.AlwaysFail:
                        return true;
                    case OutboxSendFailureKind.SucceedAfterNFailures:
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
