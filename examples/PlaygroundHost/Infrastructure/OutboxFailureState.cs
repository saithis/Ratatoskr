using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

/// <summary>Dev-only: transient failures injected into <see cref="IMessageSender.SendAsync"/> (outbox relay + direct publish).</summary>
public sealed class OutboxFailureState
{
    private readonly Lock _lock = new();
    private PlaygroundOutcomeMode _mode = PlaygroundOutcomeMode.Succeed;
    private int _failuresRemaining;

    /// <summary>When set, send failures apply only to messages carrying this run id in CloudEvents extensions.</summary>
    public string? ActiveScenarioRunId { get; private set; }

    public void SetActiveScenarioRun(string? scenarioRunId) => ActiveScenarioRunId = scenarioRunId;

    public bool TryConsumeSendFailure(MessageProperties props)
    {
        lock (_lock)
        {
            if (ActiveScenarioRunId is { } active && active.Length > 0)
            {
                if (!props.CloudEventExtensions.TryGetValue(PlaygroundCorrelation.CloudEventsExtensionKey, out var ext) ||
                    ext is not string runStr ||
                    !string.Equals(runStr, active, StringComparison.Ordinal))
                    return false;
            }

            return TryConsumeSendFailureCore();
        }
    }

    private bool TryConsumeSendFailureCore()
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

    public string Cycle()
    {
        lock (_lock)
        {
            _mode = _mode switch
            {
                PlaygroundOutcomeMode.Succeed => PlaygroundOutcomeMode.AlwaysFail,
                PlaygroundOutcomeMode.AlwaysFail => PlaygroundOutcomeMode.SucceedAfterNFailures,
                PlaygroundOutcomeMode.SucceedAfterNFailures => PlaygroundOutcomeMode.Succeed,
                _ => PlaygroundOutcomeMode.Succeed,
            };
            if (_mode == PlaygroundOutcomeMode.SucceedAfterNFailures)
                _failuresRemaining = 2;
            else
                _failuresRemaining = 0;
            return GetApi().Mode;
        }
    }

    public string Apply(string? mode, int? failureCount)
    {
        lock (_lock)
        {
            var n = failureCount is > 0 ? failureCount.Value : 2;
            switch (mode?.ToLowerInvariant())
            {
                case "succeed":
                case "off":
                    _mode = PlaygroundOutcomeMode.Succeed;
                    _failuresRemaining = 0;
                    break;
                case "fail":
                case "always-fail":
                    _mode = PlaygroundOutcomeMode.AlwaysFail;
                    _failuresRemaining = 0;
                    break;
                case "succeed-after":
                    _mode = PlaygroundOutcomeMode.SucceedAfterNFailures;
                    _failuresRemaining = n;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown mode '{mode}'.");
            }

            return GetApi().Mode;
        }
    }

    public (string Mode, int FailuresRemaining) GetApi()
    {
        lock (_lock)
        {
            return _mode switch
            {
                PlaygroundOutcomeMode.Succeed => ("succeed", 0),
                PlaygroundOutcomeMode.AlwaysFail => ("fail", 0),
                PlaygroundOutcomeMode.SucceedAfterNFailures => ("succeed-after", _failuresRemaining),
                _ => ("succeed", 0),
            };
        }
    }
}
