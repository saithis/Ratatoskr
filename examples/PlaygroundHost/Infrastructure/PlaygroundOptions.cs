namespace PlaygroundHost.Infrastructure;

public sealed class PlaygroundOptions
{
    public const string SectionName = "Playground";

    public bool Enabled { get; set; }

    public int RunTimeoutSeconds { get; set; } = 120;
}
