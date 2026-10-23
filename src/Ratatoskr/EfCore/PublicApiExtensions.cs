using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.EfCore.Management;
using Ratatoskr.Management;

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
        /// The first DbContext registered becomes the default, unless another one is marked with
        /// <c>AsDefault()</c> or set by <c>AddRatatoskr&lt;TDbContext&gt;()</c>.
        /// Throws if durability is already registered for this DbContext.
        /// </summary>
        public RatatoskrBuilder AddEfCoreDurability<TDbContext>(
            Action<DurabilityBuilder<TDbContext>> configure
        )
            where TDbContext : DbContext, IRatatoskrDbContext
        {
            ArgumentNullException.ThrowIfNull(configure);

            if (IsDurabilityRegistered<TDbContext>(builder.Services))
            {
                throw new InvalidOperationException(
                    $"AddEfCoreDurability<{typeof(TDbContext).Name}>() was called more than once. "
                        + "Configure UseInbox()/UseOutbox() in a single call."
                );
            }

            var durabilityBuilder = new DurabilityBuilder<TDbContext>();
            configure(durabilityBuilder);

            if (durabilityBuilder.InboxBuilder == null && durabilityBuilder.OutboxBuilder == null)
            {
                throw new InvalidOperationException(
                    $"AddEfCoreDurability<{typeof(TDbContext).Name}>() requires at least UseInbox() or UseOutbox() to be called."
                );
            }

            SelectDefaultDbContext<TDbContext>(builder, durabilityBuilder.IsDefault);

            if (!durabilityBuilder.RegisterBackgroundServices)
            {
                durabilityBuilder.InboxBuilder?.WithoutBackgroundProcessing();
                durabilityBuilder.OutboxBuilder?.WithoutBackgroundProcessing();
            }

            var services = builder.Services;
            RegisterSharedServicesOnce(builder);
            services.AddSingleton<DurabilityMarker<TDbContext>>();

            if (durabilityBuilder.InboxBuilder != null)
            {
                RegisterInboxServices(builder, durabilityBuilder.InboxBuilder);
            }

            if (durabilityBuilder.OutboxBuilder != null)
            {
                RegisterOutboxServices(builder, durabilityBuilder.OutboxBuilder);
            }

            services.AddSingleton(_ => new EfCoreMetricsSettings<TDbContext>(
                durabilityBuilder.MetricsPollingInterval,
                durabilityBuilder.MetricsQueryTimeout
            ));
            if (durabilityBuilder.RegisterBackgroundServices)
            {
                services.AddHostedService<EfCoreMetricsBackgroundService<TDbContext>>();
            }

            services.AddSingleton<
                IEfCoreManagementDbContextDescriptor,
                EfCoreManagementDbContextDescriptor<TDbContext>
            >();

            return builder;
        }

        /// <summary>
        /// Registers the services, validators and deferred actions that are shared by all
        /// DbContexts, once per <see cref="RatatoskrBuilder"/>.
        /// </summary>
        private static void RegisterSharedServicesOnce(RatatoskrBuilder ratatoskrBuilder)
        {
            var services = ratatoskrBuilder.Services;
            if (services.Any(d => d.ServiceType == typeof(EfCoreSharedServicesMarker)))
            {
                return;
            }

            services.AddSingleton<EfCoreSharedServicesMarker>();
            services.TryAddSingleton<EfCoreMetricsState>();
            services.TryAddSingleton<EfCoreBacklogGauges>();

            // EF Core transport sender, needed for inbox-only and outbox-only setups alike
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IMessageSender, EfCoreMessageSender>()
            );

            // Management API
            services.TryAddScoped<EfCoreManagementDbContextLookup>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IRatatoskrEndpointConfigurator,
                    EfCoreEndpointConfigurator
                >()
            );

            ratatoskrBuilder.AddValidator(EfCoreConfigurationValidator.Validate);
            ratatoskrBuilder.AddDeferredServiceAction(deferredServices =>
                ApplyDefaultDbContext(ratatoskrBuilder, deferredServices)
            );
        }

        /// <summary>
        /// Wires up the default DbContext once all configuration has run, so the result does not
        /// depend on registration order: <see cref="IRatatoskrDbContext"/> resolves to it, and consume
        /// channels with inbox-managed (keyed) handlers that neither configure an inbox nor opt out
        /// of it use its inbox.
        /// </summary>
        private static void ApplyDefaultDbContext(
            RatatoskrBuilder ratatoskrBuilder,
            IServiceCollection services
        )
        {
            if (ratatoskrBuilder.DefaultDbContextType is not { } defaultDbContextType)
            {
                return;
            }

            services.TryAddScoped<IRatatoskrDbContext>(sp =>
                (IRatatoskrDbContext)sp.GetRequiredService(defaultDbContextType)
            );

            foreach (var channel in ratatoskrBuilder.ChannelRegistry.GetConsumeChannels())
            {
                if (
                    channel.GetExtension<ChannelInboxConfig>() != null
                    || channel.GetExtension<ConsumeChannelInboxRequirementOptOut>() != null
                )
                {
                    continue;
                }

                var hasInboxHandlers = channel
                    .Messages.Select(m => m.GetExtension<MessageHandlerRegistrations>())
                    .Where(r => r != null)
                    .SelectMany(r => r!.Handlers)
                    .Any(h => h.IsInbox);

                if (hasInboxHandlers)
                {
                    channel.SetExtension(new ChannelInboxConfig(defaultDbContextType));
                }
            }
        }

        private static void RegisterInboxServices<TDbContext>(
            RatatoskrBuilder ratatoskrBuilder,
            InboxBuilder<TDbContext> inboxBuilder
        )
            where TDbContext : DbContext, IRatatoskrDbContext
        {
            if (inboxBuilder.Options.LockName == InboxOptions.DefaultLockName)
            {
                inboxBuilder.Options.LockName = $"InboxProcessor_{typeof(TDbContext).Name}";
            }

            if (inboxBuilder.Options.CleanupLockName == InboxOptions.DefaultCleanupLockName)
            {
                inboxBuilder.Options.CleanupLockName = $"InboxCleanup_{typeof(TDbContext).Name}";
            }

            ratatoskrBuilder.Services.AddSingleton(
                new InboxOptionsHolder<TDbContext>(inboxBuilder.Options)
            );
            ratatoskrBuilder.Services.AddTransient<InboxMessageProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<InboxProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<IProcessorTrigger>(sp =>
                sp.GetRequiredService<InboxProcessor<TDbContext>>()
            );
            if (inboxBuilder.RegisterBackgroundService)
            {
                ratatoskrBuilder.Services.AddSingleton<IHostedService>(sp =>
                    sp.GetRequiredService<InboxProcessor<TDbContext>>()
                );
                if (inboxBuilder.Options.RetentionPeriod.HasValue)
                {
                    ratatoskrBuilder.Services.AddSingleton<
                        IHostedService,
                        InboxCleanupService<TDbContext>
                    >();
                }
            }
            ratatoskrBuilder.Services.AddSingleton<InboxAcceptor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<IEfCoreInboxAcceptor>(sp =>
                sp.GetRequiredService<InboxAcceptor<TDbContext>>()
            );
            ratatoskrBuilder.Services.AddSingleton<
                IMessageRouteInterceptor,
                InboxRouteInterceptor<TDbContext>
            >();

            var policyAggregator = GetOrAddConsumeChannelInboxPolicyAggregator(
                ratatoskrBuilder.Services
            );
            policyAggregator.MergeRequirement(inboxBuilder.Options.ConsumeChannelInboxRequirement);
            if (policyAggregator.EffectiveRequirement == ConsumeChannelInboxRequirement.Warn)
            {
                ratatoskrBuilder.Services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<
                        IHostedService,
                        ConsumeChannelInboxWarningHostedService
                    >()
                );
            }

            ratatoskrBuilder.AddHandlerValidator(
                (channelRegistry, handlerRegistry) =>
                    InboxConfigurationValidator.Validate(
                        channelRegistry,
                        handlerRegistry,
                        policyAggregator
                    )
            );
        }

        private static ConsumeChannelInboxPolicyAggregator GetOrAddConsumeChannelInboxPolicyAggregator(
            IServiceCollection services
        )
        {
            var existing = services
                .Where(d => d.ServiceType == typeof(ConsumeChannelInboxPolicyAggregator))
                .Select(d => d.ImplementationInstance)
                .OfType<ConsumeChannelInboxPolicyAggregator>()
                .FirstOrDefault();
            if (existing != null)
            {
                return existing;
            }

            var aggregator = new ConsumeChannelInboxPolicyAggregator();
            services.AddSingleton(aggregator);
            return aggregator;
        }

        private static void RegisterOutboxServices<TDbContext>(
            RatatoskrBuilder ratatoskrBuilder,
            OutboxBuilder<TDbContext> outboxBuilder
        )
            where TDbContext : DbContext, IRatatoskrDbContext
        {
            if (outboxBuilder.Options.LockName == OutboxOptions.DefaultLockName)
            {
                outboxBuilder.Options.LockName = $"OutboxProcessor_{typeof(TDbContext).Name}";
            }

            if (outboxBuilder.Options.CleanupLockName == OutboxOptions.DefaultCleanupLockName)
            {
                outboxBuilder.Options.CleanupLockName = $"OutboxCleanup_{typeof(TDbContext).Name}";
            }

            ratatoskrBuilder.Services.AddSingleton(
                new OutboxOptionsHolder<TDbContext>(outboxBuilder.Options)
            );
            ratatoskrBuilder.Services.AddSingleton<OutboxTriggerInterceptor<TDbContext>>();
            ratatoskrBuilder.Services.AddTransient<OutboxMessageProcessor<TDbContext>>();
            ratatoskrBuilder.Services.AddSingleton<OutboxProcessor<TDbContext>>();
            if (outboxBuilder.RegisterBackgroundService)
            {
                ratatoskrBuilder.Services.AddSingleton<IHostedService>(sp =>
                    sp.GetRequiredService<OutboxProcessor<TDbContext>>()
                );
                if (outboxBuilder.Options.RetentionPeriod.HasValue)
                {
                    ratatoskrBuilder.Services.AddSingleton<
                        IHostedService,
                        OutboxCleanupService<TDbContext>
                    >();
                }
            }
        }
    }

    internal static bool IsDurabilityRegistered<TDbContext>(IServiceCollection services)
        where TDbContext : DbContext, IRatatoskrDbContext =>
        services.Any(d => d.ServiceType == typeof(DurabilityMarker<TDbContext>));

    /// <summary>
    /// Records <typeparamref name="TDbContext"/> as the default DbContext. An explicit choice
    /// (<c>AsDefault()</c> or <c>AddRatatoskr&lt;TDbContext&gt;()</c>) overrides the implicit
    /// first-registered default; two different explicit choices fail fast.
    /// </summary>
    internal static void SelectDefaultDbContext<TDbContext>(
        RatatoskrBuilder ratatoskrBuilder,
        bool explicitly
    )
        where TDbContext : DbContext, IRatatoskrDbContext
    {
        if (!explicitly)
        {
            ratatoskrBuilder.DefaultDbContextType ??= typeof(TDbContext);
            return;
        }

        if (
            ratatoskrBuilder.IsDefaultDbContextExplicit
            && ratatoskrBuilder.DefaultDbContextType != typeof(TDbContext)
        )
        {
            throw new InvalidOperationException(
                $"Cannot make {typeof(TDbContext).Name} the default DbContext: "
                    + $"{ratatoskrBuilder.DefaultDbContextType!.Name} is already the default "
                    + "(set by AddRatatoskr<TDbContext>() or AsDefault()). Only one DbContext can be the default."
            );
        }

        ratatoskrBuilder.DefaultDbContextType = typeof(TDbContext);
        ratatoskrBuilder.IsDefaultDbContextExplicit = true;
    }

    /// <summary>
    /// Registers the Ratatoskr DbContext interceptor that is responsible for converting the messages to ef core entities for saving and triggering the outbox processor afterward for faster dispatch to the broker.
    /// </summary>
    public static DbContextOptionsBuilder UseRatatoskr<TDbContext>(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider
    )
        where TDbContext : DbContext, IRatatoskrDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var interceptor = serviceProvider.GetRequiredService<
            OutboxTriggerInterceptor<TDbContext>
        >();
        return builder.AddInterceptors(interceptor);
    }
}
