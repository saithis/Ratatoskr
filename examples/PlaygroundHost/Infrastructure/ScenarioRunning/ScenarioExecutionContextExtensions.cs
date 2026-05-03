using Microsoft.Extensions.DependencyInjection;
using PlaygroundHost.Persistence;
using Ratatoskr;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public static class ScenarioExecutionContextExtensions
{
    public static TimeProvider GetTimeProvider(this ScenarioExecutionContext context) =>
        context.Services.GetRequiredService<TimeProvider>();

    public static PublisherDbContext GetPublisherDb(this ScenarioExecutionContext context) =>
        context.Services.GetRequiredService<PublisherDbContext>();

    public static IRatatoskr GetRatatoskr(this ScenarioExecutionContext context) =>
        context.Services.GetRequiredService<IRatatoskr>();

    public static T GetRequired<T>(this ScenarioExecutionContext context)
        where T : notnull =>
        context.Services.GetRequiredService<T>();
}
