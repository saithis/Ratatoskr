using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Testing;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Helper methods for configuring services in tests
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds PostgreSQL DbContext configured with test container.
    /// Includes outbox interceptor for converting staged messages to entities.
    /// </summary>
    public static IServiceCollection AddTestDbContext(
        this IServiceCollection services,
        string connectionString,
        bool withOutboxInterceptor = true)
    {
        services.AddDbContext<TestDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString);

            if (withOutboxInterceptor)
            {
                options.RegisterTestOutbox<TestDbContext>(sp);
            }
        });

        return services;
    }
}
