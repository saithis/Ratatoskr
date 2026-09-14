using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Management;
using Ratatoskr.Management.Http;
using Ratatoskr.UI.Store;

namespace Ratatoskr.UI;

/// <summary>How the dashboard keeps its own state.</summary>
public sealed class RatatoskrDashboardStoreOptions
{
    /// <summary>
    /// Applies pending migrations at startup.
    /// </summary>
    /// <remarks>
    /// Off by default: applying schema changes from application start-up is a deployment decision,
    /// not a library one, and in a multi-replica rollout every replica would race to do it. Turn it
    /// on for a single-node or local setup; otherwise run <c>MigrateAsync</c> from your deployment
    /// step.
    /// </remarks>
    public bool AutoMigrate { get; set; }
}

/// <summary>Registers the Ratatoskr management dashboard.</summary>
public static class RatatoskrDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dashboard: its store, its transport registry, its discovery consumer, and the
    /// audit trail.
    /// </summary>
    /// <remarks>
    /// A store is required rather than optional. It is what lets a restarted dashboard show the
    /// fleet immediately, what makes mutations auditable, and what turns the in-memory registry
    /// into a cache that can be lost without consequence. SQLite is supported for single-node and
    /// local setups, so the cost is one connection string.
    /// </remarks>
    public static IServiceCollection AddRatatoskrDashboard(
        this IServiceCollection services,
        Action<ManagementDashboardBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ManagementDashboardBuilder(services);
        configure(builder);
        builder.Apply();

        services.TryAddSingleton<AuditingManagementDispatchStrategy>();
        services.AddHostedService<DashboardServiceStore>();
        services.AddHostedService<DashboardAuditCleanupService>();

        if (services.All(descriptor => descriptor.ServiceType != typeof(RatatoskrDashboardDbContext)))
        {
            throw new InvalidOperationException(
                "The Ratatoskr dashboard needs a store. Call UseStore(...) inside AddRatatoskrDashboard, "
                    + "for example dashboard.UseStore(db => db.UseNpgsql(connectionString)) or UseSqlite for a local setup."
            );
        }

        return services;
    }
}

/// <summary>Store registration for the dashboard builder.</summary>
public static class ManagementDashboardBuilderStoreExtensions
{
    /// <summary>
    /// Configures the dashboard's own database.
    /// </summary>
    /// <remarks>
    /// The schema ships as EF Core migrations. Do not reach for <c>EnsureCreated</c>: it is
    /// all-or-nothing per database and silently creates nothing when the database already exists,
    /// which is exactly what happens when the dashboard points at an application database.
    /// </remarks>
    public static ManagementDashboardBuilder UseStore(
        this ManagementDashboardBuilder builder,
        Action<DbContextOptionsBuilder> configure,
        Action<RatatoskrDashboardStoreOptions>? configureStore = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddOptions<RatatoskrDashboardStoreOptions>();
        if (configureStore is not null)
        {
            builder.Services.Configure(configureStore);
        }

        builder.Services.AddDbContext<RatatoskrDashboardDbContext>(configure);
        builder.Services.AddHostedService<DashboardMigrationService>();
        return builder;
    }
}
