using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds message tracking support for integration testing.
    /// Registers a <see cref="MessageTracker"/> singleton that observes all message activities in the pipeline.
    /// </summary>
    public static IServiceCollection AddRatatoskrTesting(this IServiceCollection services)
    {
        services.AddSingleton<MessageTracker>();
        services.AddSingleton<IMessageActivityObserver>(sp => sp.GetRequiredService<MessageTracker>());
        return services;
    }
}
