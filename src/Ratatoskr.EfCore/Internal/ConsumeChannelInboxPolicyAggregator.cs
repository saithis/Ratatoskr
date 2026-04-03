namespace Ratatoskr.EfCore.Internal;

internal sealed class ConsumeChannelInboxPolicyAggregator
{
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ConsumeChannelInboxRequirement EffectiveRequirement { get; private set; } = ConsumeChannelInboxRequirement.None;

    public int WarningCount
    {
        get
        {
            lock (_sync)
                return _warnings.Count;
        }
    }

    public void MergeRequirement(ConsumeChannelInboxRequirement requirement)
    {
        if (requirement > EffectiveRequirement)
            EffectiveRequirement = requirement;
    }

    public void AddWarning(string warning)
    {
        lock (_sync)
            _warnings.Add(warning);
    }

    public string[] DrainWarnings()
    {
        lock (_sync)
        {
            var warnings = _warnings.ToArray();
            _warnings.Clear();
            return warnings;
        }
    }
}
