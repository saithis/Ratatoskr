namespace PlaygroundHost.Infrastructure;

public sealed class PlaygroundOptions
{
    public const string SectionName = "Playground";

    /// <summary>When false, playground and scenario APIs return 404 (except health).</summary>
    public bool Enabled { get; set; }

    public int RunTimeoutSeconds { get; set; } = 120;
}
