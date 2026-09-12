using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr.EfCore;
using Ratatoskr.Management;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore.Idempotency;
using Ratatoskr.Management.EfCore.Internal;
using Ratatoskr.Management.EfCore.Operations;

namespace Ratatoskr.Management.EfCore;

/// <summary>Registers the EF Core inbox and outbox management operations.</summary>
public static class ManagementEfCoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds the single implementation of every inbox and outbox management operation, reached
    /// identically by the per-service REST API, the in-process transport and any broker transport.
    /// </summary>
    /// <remarks>
    /// Operations are scoped because they hold a <c>DbContext</c>. The dispatcher creates a scope
    /// per execution, so a singleton command consumer never captures one — a failure mode that is
    /// silent in production and shows up only as intermittent corruption.
    /// </remarks>
    public static IServiceCollection AddRatatoskrManagementEfCore(
        this IServiceCollection services,
        Action<ManagementAgentOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRatatoskrManagementCore(configure);
        services.TryAddScoped<ManagementResourceResolver>();
        services.TryAddSingleton<ManagementOperationLog>();

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IManagementResourceSummaryProvider,
                EfCoreResourceSummaryProvider
            >()
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IManagementCapabilityContributor, EfCoreCapabilityContributor>()
        );

        services.AddManagementOperation<ListContextsOperation>();
        services.AddManagementOperation<ContextHealthOperation>();

        services.AddManagementOperation<ListOutboxOperation>();
        services.AddManagementOperation<CountOutboxOperation>();
        services.AddManagementOperation<GetOutboxOperation>();
        services.AddManagementOperation<RequeueOutboxOperation>();
        services.AddManagementOperation<DeleteOutboxOperation>();
        services.AddManagementOperation<RequeueMatchingOutboxOperation>();
        services.AddManagementOperation<DeleteMatchingOutboxOperation>();

        services.AddManagementOperation<ListInboxOperation>();
        services.AddManagementOperation<CountInboxOperation>();
        services.AddManagementOperation<GetInboxOperation>();
        services.AddManagementOperation<RequeueInboxOperation>();
        services.AddManagementOperation<DeleteInboxOperation>();
        services.AddManagementOperation<RequeueInboxMessageOperation>();
        services.AddManagementOperation<RequeueMatchingInboxOperation>();
        services.AddManagementOperation<DeleteMatchingInboxOperation>();

        return services;
    }

    /// <summary>
    /// Registers the retention worker that prunes completed operation records for one DbContext.
    /// </summary>
    /// <remarks>
    /// Per-context because the table lives in the application's own database, and separate from
    /// the inbox cleanup service because an outbox-only service still has an operation log to
    /// prune.
    /// </remarks>
    public static IServiceCollection AddRatatoskrManagementOperationCleanup<TDbContext>(
        this IServiceCollection services
    )
        where TDbContext : DbContext, IOutboxDbContext, IInboxDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IHostedService, ManagementOperationCleanupService<TDbContext>>();
        return services;
    }
}

/// <summary>Adds the EF Core operations to a management agent.</summary>
public static class ManagementEfCoreBuilderExtensions
{
    /// <summary>
    /// Serves the inbox and outbox operations from this agent, against every DbContext registered
    /// with <c>AddEfCoreDurability</c>.
    /// </summary>
    public static ManagementAgentBuilder UseEfCore(this ManagementAgentBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddRatatoskrManagementEfCore();
        return builder;
    }
}
