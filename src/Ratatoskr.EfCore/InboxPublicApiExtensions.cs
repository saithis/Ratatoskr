using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ratatoskr;
using Ratatoskr.Core;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Local;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods to enable the inbox pattern for durable, per-handler message delivery.
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

            builder.Services.AddSingleton(Options.Create(inboxBuilder.Options));
            builder.Services.AddSingleton<InboxProcessor<TDbContext>>();
            if (inboxBuilder.RegisterBackgroundService)
                builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<InboxProcessor<TDbContext>>());
            builder.Services.AddSingleton<IInboxInterceptor, InboxInterceptor<TDbContext>>();

            // Deferred: runs after the full configure() callback, so UseEfCoreInbox can be called
            // before or after UseLocalTransport() without breaking anything.
            builder.AddDeferredServiceAction(services =>
            {
                // Replace LocalMessageSender with the durable version if local transport is configured.
                var localSenderDescriptor = services.FirstOrDefault(
                    d => d.ServiceType == typeof(IMessageSender)
                      && d.ImplementationType == typeof(LocalMessageSender));

                if (localSenderDescriptor != null)
                {
                    services.Remove(localSenderDescriptor);
                    services.AddSingleton<IMessageSender, DurableLocalMessageSender<TDbContext>>();
                }

                // Finalize inbox handler registrations now that global config (DefaultHandlerInboxEnabled)
                // and ChannelRegistry (wire type names) are both fully known.
                var defaultEnabled = inboxBuilder.Options.DefaultHandlerInboxEnabled;
                foreach (var pending in builder.PendingHandlers)
                {
                    var useInbox = pending.ExplicitUseInbox ?? defaultEnabled;
                    if (!useInbox) continue;

                    var key = pending.ExplicitKey ?? pending.HandlerType.FullName!;

                    // Resolve wire type name: prefer ChannelRegistry config (accounts for per-message
                    // overrides), fall back to [RatatoskrMessage] attribute.
                    var wireTypeName = ResolveWireTypeName(builder.ChannelRegistry, pending.MessageType);

                    builder.InboxHandlerRegistry.Register(key, pending.MessageType, pending.HandlerType, wireTypeName);
                }
            });

            // Startup validation runs after deferred actions (InboxHandlerRegistry is fully populated).
            builder.AddValidator(cr => InboxConfigurationValidator.Validate(cr, builder.InboxHandlerRegistry));

            return builder;
        }
    }

    /// <summary>
    /// Adds the necessary inbox entities to the DB model.
    /// Call this inside <c>OnModelCreating</c> of your DbContext.
    /// </summary>
    public static void AddInboxEntities(this ModelBuilder modelBuilder)
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

            entity.HasIndex(
                e => new { e.CompletedAt, e.IsPoisoned, e.NextAttemptAt, e.ProcessingStartedAt, e.MessageId },
                "IX_InboxHandlerStatuses_Processing")
            .HasFilter("\"CompletedAt\" IS NULL AND \"IsPoisoned\" = false");

            entity.Property(e => e.HandlerKey).HasMaxLength(200).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);

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
    private static string? ResolveWireTypeName(ChannelRegistry registry, Type messageType)
    {
        foreach (var channel in registry.GetConsumeChannels())
        {
            var msg = channel.Messages.FirstOrDefault(m => m.MessageType == messageType);
            if (msg != null)
                return msg.MessageTypeName;
        }

        return messageType.GetCustomAttribute<RatatoskrMessageAttribute>()?.Type;
    }
}
