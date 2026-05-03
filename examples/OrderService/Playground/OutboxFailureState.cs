namespace OrderService.Playground;

/// <summary>Dev-only: transient failures injected into <see cref="Ratatoskr.Core.IMessageSender.SendAsync"/> (outbox relay + direct publish).</summary>
public sealed class OutboxFailureState
{
    private readonly Lock _lock = new();
    private PlaygroundMessages.PlaygroundOutcomeMode _mode = PlaygroundMessages.PlaygroundOutcomeMode.Succeed;
    private int _failuresRemaining;

    /// <summary>True when this send should throw (simulated broker outage).</summary>
    public bool TryConsumeSendFailure()
    {
        lock (_lock)
        {
            switch (_mode)
            {
                case PlaygroundMessages.PlaygroundOutcomeMode.Succeed:
                    return false;
                case PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail:
                    return true;
                case PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures:
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

    public string Cycle()
    {
        lock (_lock)
        {
            _mode = _mode switch
            {
                PlaygroundMessages.PlaygroundOutcomeMode.Succeed => PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail,
                PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail => PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures,
                PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures => PlaygroundMessages.PlaygroundOutcomeMode.Succeed,
                _ => PlaygroundMessages.PlaygroundOutcomeMode.Succeed,
            };
            if (_mode == PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures)
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
                    _mode = PlaygroundMessages.PlaygroundOutcomeMode.Succeed;
                    _failuresRemaining = 0;
                    break;
                case "fail":
                case "always-fail":
                    _mode = PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail;
                    _failuresRemaining = 0;
                    break;
                case "succeed-after":
                    _mode = PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures;
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
                PlaygroundMessages.PlaygroundOutcomeMode.Succeed => ("succeed", 0),
                PlaygroundMessages.PlaygroundOutcomeMode.AlwaysFail => ("fail", 0),
                PlaygroundMessages.PlaygroundOutcomeMode.SucceedAfterNFailures => ("succeed-after", _failuresRemaining),
                _ => ("succeed", 0),
            };
        }
    }
}
