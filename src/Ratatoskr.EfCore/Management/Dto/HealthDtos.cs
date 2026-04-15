namespace Ratatoskr.EfCore.Management.Dto;

internal record HealthOverviewDto(List<DbContextHealthDto> DbContexts);

internal record DbContextHealthDto(
    string DbContextName,
    long PoisonedOutboxCount,
    long PoisonedInboxCount,
    long PendingOutboxCount,
    long PendingInboxCount,
    DateTimeOffset? LastOutboxProcessedAt,
    DateTimeOffset? LastInboxProcessedAt);
