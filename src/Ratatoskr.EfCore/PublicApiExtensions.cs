using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Contains the extension methods to enable/configure EF Core durability (inbox and outbox).
/// </summary>
public static class PublicApiExtensions
{
    extension(RatatoskrBuilder builder)
    {
        /// <summary>
        /// Registers EF Core durability (inbox and/or outbox) for the given DbContext.
        /// Call <c>UseInbox()</c> and/or <c>UseOutbox()</c> on the builder to enable each pattern.
        /// Per-DbContext services are registered once (idempotent).
        /// </summary>
        public RatatoskrBuilder AddEfCoreDurability<TDbContext>(Action<DurabilityBuilder<TDbContext>> configure)
            where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
        {
            // Idempotency: skip if already registered for this DbContext type
            if (builder.Services.Any(d => d.ServiceType == typeof(DurabilityMarker<TDbContext>)))
                return builder;

            builder.Services.AddSingleton<DurabilityMarker<TDbContext>>();

            var durabilityBuilder = new DurabilityBuilder<TDbContext>();
            configure(durabilityBuilder);

            if (durabilityBuilder.InboxBuilder == null && durabilityBuilder.OutboxBuilder == null)
                throw new InvalidOperationException(
                    $"AddEfCoreDurability<{typeof(TDbContext).Name}>() requires at least UseInbox() or UseOutbox() to be called.");

            if (durabilityBuilder.InboxBuilder != null)
                RegisterInboxServices<TDbContext>(builder, durabilityBuilder.InboxBuilder);

            if (durabilityBuilder.OutboxBuilder != null)
                RegisterOutboxServices<TDbContext>(builder, durabilityBuilder.OutboxBuilder);

            return builder;
        }

        private static void RegisterInboxServices<TDbContext>(
            RatatoskrBuilder ratatoskrBuilder, InboxBuilder<TDbContext> inboxBuilder)
            where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
        {
            if (inboxBuilder.Options.LockName == InboxOptions.DefaultLockName)
                inboxBuilder.Options.LockName = $"InboxProcessor_{typeof(TDbContext).Name}";

            ratatoskrBuilder.Services.AddSingleton(new InboxOptionsHolder<TDbContext>(inboxBuilder.Options));
            ratatoskrBuilder.Services.TryAddSingleton<InboxTelemetry>();
            ratatoskrBuilder.Services.AddTransient<InboxMessageProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<InboxProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<IProcessorTrigger>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
            if (inboxBuilder.RegisterBackgroundService)
                ratatoskrBuilder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
            ratatoskrBuilder.Services.AddSingleton<InboxAcceptor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<IMessageRouteInterceptor, InboxRouteInterceptor<TDbContext>>();

            ratatoskrBuilder.AddHandlerValidator(InboxConfigurationValidator.Validate);
        }

        private static void RegisterOutboxServices<TDbContext>(
            RatatoskrBuilder ratatoskrBuilder, OutboxBuilder<TDbContext> outboxBuilder)
            where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
        {
            if (outboxBuilder.Options.LockName == OutboxOptions.DefaultLockName)
                outboxBuilder.Options.LockName = $"OutboxProcessor_{typeof(TDbContext).Name}";

            ratatoskrBuilder.Services.AddSingleton(new OutboxOptionsHolder<TDbContext>(outboxBuilder.Options));
            ratatoskrBuilder.Services.TryAddSingleton<OutboxTelemetry>();
            ratatoskrBuilder.Services.AddSingleton<OutboxTriggerInterceptor<TDbContext>>();
            ratatoskrBuilder.Services.AddTransient<OutboxMessageProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<OutboxProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxProcessor<TDbContext>>());
        }
    }

    /// <summary>
    /// Registers the DbContext interceptor that is responsible for converting the messages to ef core entities for saving and triggering the outbox processor afterward for faster dispatch to the broker.
    /// </summary>
    public static DbContextOptionsBuilder RegisterOutbox<TDbContext>(this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
        where TDbContext : DbContext, IOutboxDbContext
    {
        var interceptor = serviceProvider.GetRequiredService<OutboxTriggerInterceptor<TDbContext>>();
        return builder.AddInterceptors(interceptor);
    }

    /// <summary>
    /// Adds the necessary outbox entities to the DB model.
    /// </summary>
    public static void AddOutboxEntities(this ModelBuilder modelBuilder) =>
        modelBuilder.AddOutboxEntities(database: null);

    /// <summary>
    /// Adds the necessary outbox entities to the DB model.
    /// When <paramref name="database"/> is provided, a partial/filtered index is applied
    /// for supported providers (PostgreSQL, SQL Server) to improve query performance on large tables.
    /// </summary>
    public static void AddOutboxEntities(this ModelBuilder modelBuilder, DatabaseFacade? database)
    {
        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            var index = entity.HasIndex(
                e => new {
                    e.ProcessedAt,
                    e.IsPoisoned,
                    e.NextAttemptAt,
                    e.ProcessingStartedAt,
                    e.CreatedAt
                },
                "IX_OutboxMessages_Processing");

            var filter = DatabaseProviderHelper.GetOutboxProcessingFilter(database);
            if (filter != null)
                index.HasFilter(filter);

            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.SerializedProperties).IsRequired();
            entity.Property(e => e.TransportName).HasMaxLength(50).IsRequired();
        });
    }
}
