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
                throw new InvalidOperationException(
                    $"AddEfCoreDurability<{typeof(TDbContext).Name}>() was called more than once. Merge UseInbox()/UseOutbox() into a single registration.");

            builder.Services.AddSingleton<DurabilityMarker<TDbContext>>();
            builder.Services.TryAddSingleton<EfCoreMetricsState>();
            builder.Services.TryAddSingleton<EfCoreBacklogGauges>();

            var durabilityBuilder = new DurabilityBuilder<TDbContext>();
            configure(durabilityBuilder);

            if (durabilityBuilder.InboxBuilder == null && durabilityBuilder.OutboxBuilder == null)
                throw new InvalidOperationException(
                    $"AddEfCoreDurability<{typeof(TDbContext).Name}>() requires at least UseInbox() or UseOutbox() to be called.");

            if (durabilityBuilder.InboxBuilder != null)
                RegisterInboxServices<TDbContext>(builder, durabilityBuilder.InboxBuilder);

            if (durabilityBuilder.OutboxBuilder != null)
                RegisterOutboxServices<TDbContext>(builder, durabilityBuilder.OutboxBuilder);

            builder.Services.AddSingleton(_ => new EfCoreMetricsSettings<TDbContext>(
                durabilityBuilder.MetricsPollingInterval,
                durabilityBuilder.MetricsQueryTimeout));
            builder.Services.AddHostedService<EfCoreMetricsBackgroundService<TDbContext>>();

            return builder;
        }

        private static void RegisterInboxServices<TDbContext>(
            RatatoskrBuilder ratatoskrBuilder, InboxBuilder<TDbContext> inboxBuilder)
            where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
        {
            if (inboxBuilder.Options.LockName == InboxOptions.DefaultLockName)
                inboxBuilder.Options.LockName = $"InboxProcessor_{typeof(TDbContext).Name}";
            if (inboxBuilder.Options.CleanupLockName == InboxOptions.DefaultCleanupLockName)
                inboxBuilder.Options.CleanupLockName = $"InboxCleanup_{typeof(TDbContext).Name}";

            ratatoskrBuilder.Services.AddSingleton(new InboxOptionsHolder<TDbContext>(inboxBuilder.Options));
            ratatoskrBuilder.Services.TryAddSingleton<InboxTelemetry>();
            ratatoskrBuilder.Services.AddTransient<InboxMessageProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<InboxProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<IProcessorTrigger>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
            if (inboxBuilder.RegisterBackgroundService)
            {
                ratatoskrBuilder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
                if (inboxBuilder.Options.RetentionPeriod.HasValue)
                    ratatoskrBuilder.Services.AddSingleton<IHostedService, InboxCleanupService<TDbContext>>();
            }
            ratatoskrBuilder.Services.AddSingleton<InboxAcceptor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<IEfCoreInboxAcceptor>(sp => sp.GetRequiredService<InboxAcceptor<TDbContext>>());
            ratatoskrBuilder.Services.AddSingleton<IMessageRouteInterceptor, InboxRouteInterceptor<TDbContext>>();

            // EF Core transport services (registered once, idempotent)
            ratatoskrBuilder.Services.TryAddSingleton<EfCoreTelemetry>();
            ratatoskrBuilder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageSender, EfCoreMessageSender>());

            ratatoskrBuilder.AddHandlerValidator(InboxConfigurationValidator.Validate);
            ratatoskrBuilder.AddValidator(EfCoreConfigurationValidator.Validate);
        }

        private static void RegisterOutboxServices<TDbContext>(
            RatatoskrBuilder ratatoskrBuilder, OutboxBuilder<TDbContext> outboxBuilder)
            where TDbContext : DbContext, IInboxDbContext, IOutboxDbContext
        {
            if (outboxBuilder.Options.LockName == OutboxOptions.DefaultLockName)
                outboxBuilder.Options.LockName = $"OutboxProcessor_{typeof(TDbContext).Name}";
            if (outboxBuilder.Options.CleanupLockName == OutboxOptions.DefaultCleanupLockName)
                outboxBuilder.Options.CleanupLockName = $"OutboxCleanup_{typeof(TDbContext).Name}";

            ratatoskrBuilder.Services.AddSingleton(new OutboxOptionsHolder<TDbContext>(outboxBuilder.Options));
            ratatoskrBuilder.Services.TryAddSingleton<OutboxTelemetry>();

            // EF Core transport services (registered once, idempotent — needed for outbox-only setups too)
            ratatoskrBuilder.Services.TryAddSingleton<EfCoreTelemetry>();
            ratatoskrBuilder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageSender, EfCoreMessageSender>());
            ratatoskrBuilder.Services.AddSingleton<OutboxTriggerInterceptor<TDbContext>>();
            ratatoskrBuilder.Services.AddTransient<OutboxMessageProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<OutboxProcessor<TDbContext>>();
            if (outboxBuilder.RegisterBackgroundService)
            {
                ratatoskrBuilder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OutboxProcessor<TDbContext>>());
                if (outboxBuilder.Options.RetentionPeriod.HasValue)
                    ratatoskrBuilder.Services.AddSingleton<IHostedService, OutboxCleanupService<TDbContext>>();
            }

            ratatoskrBuilder.AddValidator(EfCoreConfigurationValidator.Validate);
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

}
