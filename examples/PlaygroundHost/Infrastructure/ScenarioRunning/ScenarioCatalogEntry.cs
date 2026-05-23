namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public sealed record ScenarioCatalogEntry(
    string Slug,
    string Title,
    string Description,
    string Topic,
    bool RequiresDangerConfirmation,
    string? DangerConfirmationText
);
