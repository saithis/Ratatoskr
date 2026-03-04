using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ratatoskr;
using Ratatoskr.Config;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods to enable the inbox pattern for durable message delivery.
/// Inbox is configured per message type on consume channels.
/// </summary>
public static class InboxPublicApiExtensions
{
    /// <summary>
    /// Build-time state for inbox configuration, shared across all
    /// <c>UseEfCoreInbox</c> and <c>UseInbox</c> calls on the same builder.
    /// Stored in the builder's extension bag via <see cref="RatatoskrBuilder.GetOrSetExtension{T}"/>.
    /// </summary>
    private class InboxBuildTimeState
    {
        public InboxHandlerRegistry HandlerRegistry { get; } = new();
        public InboxRoutingTable RoutingTable { get; } = new();
        public InboxDbContextRegistry DbContextRegistry { get; } = new();
        public HashSet<Type> RegisteredDbContextTypes { get; } = new();
        public Dictionary<Type, bool> RegisterBackgroundService { get; } = new();
        public bool SharedRegistrationDone { get; set; }
    }

    private static InboxBuildTimeState GetOrCreateState(RatatoskrBuilder builder) =>
        builder.GetOrSetExtension(() => new InboxBuildTimeState());

    extension(RatatoskrBuilder builder)
    {
        /// <summary>
        /// Registers the inbox pattern with default options for the specified DbContext.
        /// </summary>
        public RatatoskrBuilder UseEfCoreInbox<TDbContext>()
            where TDbContext : DbContext, IInboxDbContext
        {
            return builder.UseEfCoreInbox<TDbContext>(configure: null);
        }

        /// <summary>
        /// Registers the inbox pattern with custom options for the specified DbContext.
        /// May be called before or after <c>UseLocalTransport()</c> — order does not matter.
        /// Can be called multiple times for different DbContext types.
        /// </summary>
        public RatatoskrBuilder UseEfCoreInbox<TDbContext>(Action<InboxBuilder<TDbContext>>? configure)
            where TDbContext : DbContext, IInboxDbContext
        {
            var state = GetOrCreateState(builder);

            if (state.DbContextRegistry.ContainsOptions(typeof(TDbContext)))
                throw new InvalidOperationException(
                    $"UseEfCoreInbox<{typeof(TDbContext).Name}>() has already been called. " +
                    $"Each DbContext type can only be registered once for the inbox.");

            var inboxBuilder = new InboxBuilder<TDbContext>();
            configure?.Invoke(inboxBuilder);

            // Set lock name per DbContext type if still the default
            if (inboxBuilder.Options.LockName == "InboxProcessor")
                inboxBuilder.Options.LockName = $"InboxProcessor-{typeof(TDbContext).Name}";

            state.DbContextRegistry.RegisterOptions(typeof(TDbContext), inboxBuilder.Options);
            state.RegisterBackgroundService[typeof(TDbContext)] = inboxBuilder.RegisterBackgroundService;

            // Register per-DbContext infrastructure (idempotent per type)
            RegisterPerDbContextServices<TDbContext>(builder, state);

            // Ensure shared registries and deferred action are set up (once)
            EnsureSharedRegistration(builder, state);

            return builder;
        }
    }

    extension(ConsumeChannelBuilder builder)
    {
        /// <summary>
        /// Associates this consume channel with a specific DbContext for inbox storage.
        /// Must be called on channels that have messages with <c>UseInbox()</c>.
        /// </summary>
        public ConsumeChannelBuilder UseInbox<TDbContext>()
            where TDbContext : DbContext, IInboxDbContext
        {
            builder.WithExtension(new InboxChannelOptions(typeof(TDbContext)));
            return builder;
        }
    }

    extension(MessageBuilder builder)
    {
        /// <summary>
        /// Routes this message type through the durable inbox on its consume channel.
        /// All handlers for this message type will be invoked by the inbox processor
        /// with independent retry and poison tracking per handler.
        /// </summary>
        public MessageBuilder UseInbox()
        {
            builder.MessageRegistration.SetExtension(new InboxMessageOptions { UseInbox = true });
            return builder;
        }
    }

    /// <summary>
    /// Registers per-DbContext services (processor, acceptor, hosted service).
    /// Idempotent — safe to call multiple times for the same type.
    /// </summary>
    private static void RegisterPerDbContextServices<TDbContext>(
        RatatoskrBuilder builder, InboxBuildTimeState state)
        where TDbContext : DbContext, IInboxDbContext
    {
        if (!state.RegisteredDbContextTypes.Add(typeof(TDbContext)))
            return;

        builder.Services.AddTransient<InboxMessageProcessor<TDbContext>>();

        // Use factory to register the processor in the trigger registry on first resolution
        builder.Services.AddSingleton(sp =>
        {
            var processor = ActivatorUtilities.CreateInstance<InboxProcessor<TDbContext>>(sp);
            sp.GetRequiredService<InboxDbContextRegistry>().RegisterTrigger(typeof(TDbContext), processor);
            return processor;
        });

        builder.Services.AddSingleton<InboxAcceptor<TDbContext>>();
        builder.Services.AddSingleton<IInboxAcceptor>(sp => sp.GetRequiredService<InboxAcceptor<TDbContext>>());
        builder.Services.AddSingleton<InboxCleanupProcessor<TDbContext>>();

        // Background service registration is deferred to the shared deferred action
        // because auto-registered DbContexts (from channel UseInbox<T>()) always register it.
    }

    /// <summary>
    /// Registers per-DbContext services using open generic types for auto-registration
    /// (when <c>UseInbox&lt;T&gt;()</c> on a channel references a DbContext not explicitly
    /// configured via <c>UseEfCoreInbox&lt;T&gt;()</c>).
    /// </summary>
    private static void AutoRegisterPerDbContextServices(
        IServiceCollection services, InboxBuildTimeState state, Type dbContextType)
    {
        if (!state.RegisteredDbContextTypes.Add(dbContextType))
            return;

        var processorType = typeof(InboxMessageProcessor<>).MakeGenericType(dbContextType);
        var bgProcessorType = typeof(InboxProcessor<>).MakeGenericType(dbContextType);
        var acceptorType = typeof(InboxAcceptor<>).MakeGenericType(dbContextType);

        services.AddTransient(processorType);

        // Use factory to register in trigger registry on first resolution
        services.AddSingleton(bgProcessorType, sp =>
        {
            var processor = ActivatorUtilities.CreateInstance(sp, bgProcessorType);
            sp.GetRequiredService<InboxDbContextRegistry>().RegisterTrigger(dbContextType, (IProcessorTrigger)processor);
            return processor;
        });

        services.AddSingleton(acceptorType);
        services.AddSingleton(typeof(IInboxAcceptor), sp => sp.GetRequiredService(acceptorType));

        var cleanupProcessorType = typeof(InboxCleanupProcessor<>).MakeGenericType(dbContextType);
        services.AddSingleton(cleanupProcessorType);

        // Auto-registered DbContexts always have background processing enabled
        state.RegisterBackgroundService[dbContextType] = true;

        // Default options with auto-generated lock name
        if (!state.DbContextRegistry.ContainsOptions(dbContextType))
            state.DbContextRegistry.RegisterOptions(dbContextType, new InboxOptions { LockName = $"InboxProcessor-{dbContextType.Name}" });
    }

    /// <summary>
    /// Ensures shared singletons, the composite interceptor, the deferred action,
    /// and the validator are registered exactly once.
    /// </summary>
    private static void EnsureSharedRegistration(RatatoskrBuilder builder, InboxBuildTimeState state)
    {
        if (state.SharedRegistrationDone)
            return;
        state.SharedRegistrationDone = true;

        // Register shared singletons (eagerly created instances)
        builder.Services.AddSingleton(state.HandlerRegistry);
        builder.Services.AddSingleton(state.RoutingTable);
        builder.Services.AddSingleton(state.DbContextRegistry);
        builder.Services.TryAddSingleton<InboxTelemetry>();

        // Register the composite route interceptor
        builder.Services.AddSingleton<IMessageRouteInterceptor, CompositeInboxRouteInterceptor>();

        // Deferred action: runs after all configuration calls complete
        builder.AddDeferredServiceAction(services =>
        {
            // 0. Validate UseInbox() is not used on publish channels
            foreach (var channel in builder.ChannelRegistry.GetPublishChannels())
            {
                foreach (var message in channel.Messages)
                {
                    var msgOpts = message.GetExtension<InboxMessageOptions>();
                    if (msgOpts is { UseInbox: true })
                    {
                        throw new InvalidOperationException(
                            $"UseInbox() was called on message type '{message.MessageTypeName}' on publish channel " +
                            $"'{channel.ChannelName}'. UseInbox() is only supported on consume channels.");
                    }
                }
            }

            // 1. Populate InboxRoutingTable from consume channel config
            var inboxMessageTypes = new HashSet<Type>();

            foreach (var channel in builder.ChannelRegistry.GetConsumeChannels())
            {
                var channelInboxOpts = channel.GetExtension<InboxChannelOptions>();
                var hasInboxMessages = false;

                foreach (var message in channel.Messages)
                {
                    var msgOpts = message.GetExtension<InboxMessageOptions>();
                    if (msgOpts is { UseInbox: true })
                    {
                        state.RoutingTable.RegisterMessage(channel.ChannelName, message.MessageTypeName);
                        inboxMessageTypes.Add(message.MessageType);
                        hasInboxMessages = true;
                    }
                }

                if (!hasInboxMessages)
                    continue;

                // Resolve the DbContext type for this channel — requires explicit UseInbox<TDbContext>()
                var dbContextType = channelInboxOpts?.DbContextType;
                if (dbContextType == null)
                {
                    throw new InvalidOperationException(
                        $"Channel '{channel.ChannelName}' has inbox-managed messages (UseInbox()) " +
                        $"but no DbContext is configured. Call UseInbox<TDbContext>() on the channel.");
                }

                state.RoutingTable.RegisterChannel(channel.ChannelName, dbContextType);

                // Auto-register infrastructure if this DbContext wasn't explicitly configured
                AutoRegisterPerDbContextServices(services, state, dbContextType);
            }

            // 2. Register hosted services for each DbContext type
            foreach (var dbContextType in state.RegisteredDbContextTypes)
            {
                if (state.RegisterBackgroundService.GetValueOrDefault(dbContextType, true))
                {
                    var bgProcessorType = typeof(InboxProcessor<>).MakeGenericType(dbContextType);
                    services.AddSingleton(typeof(IHostedService), sp => sp.GetRequiredService(bgProcessorType));
                }

                // Register cleanup processor as hosted service if any retention is configured
                var opts = state.DbContextRegistry.GetOptions(dbContextType);
                if (opts.CompletedRetention != null || opts.PoisonedRetention != null)
                {
                    var cleanupType = typeof(InboxCleanupProcessor<>).MakeGenericType(dbContextType);
                    services.AddSingleton(typeof(IHostedService), sp => sp.GetRequiredService(cleanupType));
                }
            }

            // 3. Populate InboxHandlerRegistry with all handlers for inbox-managed messages
            foreach (var pending in builder.PendingHandlers)
            {
                if (!inboxMessageTypes.Contains(pending.MessageType))
                    continue;

                var key = pending.Key
                    ?? throw new InvalidOperationException(
                        $"Inbox handler '{pending.HandlerType.Name}' does not have a stable key. " +
                        $"Add [HandlerKey(\"...\")] to the handler class or pass a key to " +
                        $"AddHandler<{pending.MessageType.Name}, {pending.HandlerType.Name}>(\"...\").");
                var wireTypeName = ResolveWireTypeName(builder.ChannelRegistry, pending.MessageType);
                state.HandlerRegistry.Register(key, pending.MessageType, pending.HandlerType, wireTypeName);
            }
        });

        // Startup validation
        builder.AddValidator(cr =>
            InboxConfigurationValidator.Validate(cr, state.RoutingTable, state.HandlerRegistry));
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
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="database">
    /// The <see cref="DatabaseFacade"/> from your DbContext (<c>this.Database</c> in <c>OnModelCreating</c>).
    /// Pass this to enable provider-specific partial indexes.
    /// </param>
    public static void AddInboxEntities(this ModelBuilder modelBuilder, DatabaseFacade? database)
    {
        modelBuilder.Entity<InboxMessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ChannelName).HasMaxLength(200).IsRequired();
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

            // Apply a partial/filtered index for supported providers.
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

    /// <summary>
    /// Resolves the wire type name for a message CLR type by:
    /// 1. Checking all registered consume channels (accounts for per-message config overrides).
    /// 2. Falling back to the [RatatoskrMessage] attribute on the CLR type.
    /// </summary>
    internal static string? ResolveWireTypeName(ChannelRegistry registry, Type messageType)
    {
        var names = registry.GetConsumeChannels()
            .SelectMany(c => c.Messages.Where(m => m.MessageType == messageType)
            .Select(m => m.MessageTypeName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return names.Length switch
        {
            > 1 => throw new InvalidOperationException(
                $"Message type '{messageType.FullName}' is mapped to multiple wire names: {string.Join(", ", names)}."),
            1 => names[0],
            _ => messageType.GetCustomAttribute<RatatoskrMessageAttribute>()?.Type
        };
    }
}
