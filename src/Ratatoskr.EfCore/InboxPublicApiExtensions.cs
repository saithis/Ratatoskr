using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods to enable the inbox pattern for durable, per-handler message delivery.
/// </summary>
public static class InboxPublicApiExtensions
{
    /// <summary>
    /// Enables the inbox pattern on this consume channel with default options.
    /// All handlers registered with a stable key on this channel will be inbox-managed.
    /// </summary>
    public static ConsumeChannelBuilder UseInbox<TDbContext>(this ConsumeChannelBuilder builder)
        where TDbContext : DbContext, IInboxDbContext
    {
        return builder.UseInbox<TDbContext>(configure: null);
    }

    /// <summary>
    /// Enables the inbox pattern on this consume channel with custom options.
    /// All handlers registered with a stable key on this channel will be inbox-managed.
    /// Per-DbContext services are registered once (idempotent across multiple channels sharing a DbContext).
    /// </summary>
    public static ConsumeChannelBuilder UseInbox<TDbContext>(this ConsumeChannelBuilder builder,
        Action<InboxBuilder<TDbContext>>? configure)
        where TDbContext : DbContext, IInboxDbContext
    {
        // Mark this channel as inbox-enabled
        builder.Channel.SetExtension(new ChannelInboxConfig(typeof(TDbContext)));

        var inboxBuilder = new InboxBuilder<TDbContext>();
        configure?.Invoke(inboxBuilder);

        var services = builder.Services;

        // Skip if already registered for this DbContext type (idempotent across multiple channels sharing a DbContext)
        if (services.Any(d => d.ServiceType == typeof(InboxOptionsHolder<TDbContext>)))
            return builder;

        // Default lock name includes DbContext type to avoid collisions across different DbContexts
        if (inboxBuilder.Options.LockName == InboxOptions.DefaultLockName)
            inboxBuilder.Options.LockName = $"InboxProcessor_{typeof(TDbContext).Name}";

        services.AddSingleton(new InboxOptionsHolder<TDbContext>(inboxBuilder.Options));
        services.TryAddSingleton<InboxTelemetry>();
        services.AddTransient<InboxMessageProcessor<TDbContext>>();
        services.AddSingleton<InboxProcessor<TDbContext>>();
        services.AddSingleton<IProcessorTrigger>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
        if (inboxBuilder.RegisterBackgroundService)
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
        services.AddSingleton<InboxAcceptor<TDbContext>>();
        services.AddSingleton<IMessageRouteInterceptor, InboxRouteInterceptor<TDbContext>>();

        // Register inbox configuration validator (runs once, after ChannelHandlerRegistry is built)
        builder.RatatoskrBuilder.AddHandlerValidator(InboxConfigurationValidator.Validate);

        return builder;
    }

    /// <summary>
    /// Adds the necessary inbox entities to the DB model.
    /// Call this inside <c>OnModelCreating</c> of your DbContext.
    /// </summary>
    public static void AddInboxEntities(this ModelBuilder modelBuilder) =>
        modelBuilder.AddInboxEntities(database: null);

    /// <summary>
    /// Adds the necessary inbox entities to the DB model.
    /// When <paramref name="database"/> is provided, a partial/filtered index is applied
    /// for supported providers (PostgreSQL, SQL Server) to improve query performance on large tables.
    /// </summary>
    public static void AddInboxEntities(this ModelBuilder modelBuilder, DatabaseFacade? database)
    {
        modelBuilder.Entity<InboxMessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TransportName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SerializedProperties).IsRequired();
        });

        modelBuilder.Entity<InboxHandlerStatusEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.MessageId, e.HandlerKey })
                .IsUnique()
                .HasDatabaseName("UX_InboxHandlerStatuses_MessageId_HandlerKey");

            var processingIndex = entity.HasIndex(
                e => new { e.CompletedAt, e.IsPoisoned, e.NextAttemptAt, e.ProcessingStartedAt, e.MessageId },
                "IX_InboxHandlerStatuses_Processing");

            var filter = DatabaseProviderHelper.GetInboxProcessingFilter(database);
            if (filter != null)
                processingIndex.HasFilter(filter);

            entity.Property(e => e.HandlerKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();

            entity.HasOne<InboxMessageEntity>()
                .WithMany()
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
