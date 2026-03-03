using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
    extension(RatatoskrBuilder builder)
    {
        /// <summary>
        /// Registers the inbox pattern with default options.
        /// </summary>
        public RatatoskrBuilder UseEfCoreInbox<TDbContext>()
            where TDbContext : DbContext, IInboxDbContext
        {
            return builder.UseEfCoreInbox<TDbContext>(configure: null);
        }

        /// <summary>
        /// Registers the inbox pattern with custom options.
        /// May be called before or after <c>UseLocalTransport()</c> — order does not matter.
        /// </summary>
        public RatatoskrBuilder UseEfCoreInbox<TDbContext>(Action<InboxBuilder<TDbContext>>? configure)
            where TDbContext : DbContext, IInboxDbContext
        {
            var inboxBuilder = new InboxBuilder<TDbContext>();
            configure?.Invoke(inboxBuilder);

            var inboxHandlerRegistry = new InboxHandlerRegistry();
            var inboxMessageRegistry = new InboxMessageRegistry();

            builder.Services.AddSingleton(Options.Create(inboxBuilder.Options));
            builder.Services.AddSingleton<InboxTelemetry>();
            builder.Services.AddSingleton(inboxHandlerRegistry);
            builder.Services.AddSingleton(inboxMessageRegistry);
            builder.Services.AddTransient<InboxMessageProcessor<TDbContext>>();
            builder.Services.AddSingleton<InboxProcessor<TDbContext>>();
            builder.Services.AddSingleton<IProcessorTrigger>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
            if (inboxBuilder.RegisterBackgroundService)
                builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
            builder.Services.AddSingleton<InboxAcceptor<TDbContext>>();
            builder.Services.AddSingleton<IMessageRouteInterceptor, InboxRouteInterceptor<TDbContext>>();

            // Deferred: runs after the full configure() callback, so UseEfCoreInbox can be called
            // before or after UseLocalTransport() without breaking anything.
            builder.AddDeferredServiceAction(services =>
            {
                // 1. Populate InboxMessageRegistry from consume channel message configuration.
                var inboxMessageTypes = new HashSet<Type>();
                foreach (var channel in builder.ChannelRegistry.GetConsumeChannels())
                {
                    foreach (var message in channel.Messages)
                    {
                        var inboxOpts = message.GetExtension<InboxMessageOptions>();
                        if (inboxOpts is { UseInbox: true })
                        {
                            inboxMessageRegistry.Register(channel.ChannelName, message.MessageTypeName);
                            inboxMessageTypes.Add(message.MessageType);
                        }
                    }
                }

                // 2. Register ALL handlers for inbox-managed message types in the handler registry.
                //    Keys are auto-generated from handler type full name.
                foreach (var pending in builder.PendingHandlers)
                {
                    if (!inboxMessageTypes.Contains(pending.MessageType))
                        continue;

                    var key = pending.HandlerType.FullName!;

                    // Resolve wire type name: prefer ChannelRegistry config (accounts for per-message
                    // overrides), fall back to [RatatoskrMessage] attribute.
                    var wireTypeName = ResolveWireTypeName(builder.ChannelRegistry, pending.MessageType);

                    inboxHandlerRegistry.Register(key, pending.MessageType, pending.HandlerType, wireTypeName);
                }
            });

            // Startup validation runs after deferred actions.
            builder.AddValidator(cr => InboxConfigurationValidator.Validate(cr, inboxMessageRegistry, inboxHandlerRegistry));

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
