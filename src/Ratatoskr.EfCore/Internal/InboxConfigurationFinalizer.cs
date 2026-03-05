using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Encapsulates the deferred configuration logic that runs after all
/// <c>UseEfCoreInbox</c> / <c>UseInbox</c> calls have completed.
/// Extracted from <see cref="InboxPublicApiExtensions.EnsureSharedRegistration"/>
/// for readability and testability.
/// </summary>
internal class InboxConfigurationFinalizer(
    ChannelRegistry channelRegistry,
    IReadOnlyList<HandlerRegistration> pendingHandlers,
    InboxHandlerRegistry handlerRegistry,
    InboxRoutingTable routingTable,
    InboxDbContextRegistry dbContextRegistry,
    HashSet<Type> registeredDbContextTypes,
    Dictionary<Type, bool> registerBackgroundService)
{
    public void Finalize(IServiceCollection services)
    {
        ValidatePublishChannels();
        var inboxMessageTypes = PopulateRoutingTable();
        RegisterHostedServices(services);
        PopulateHandlerRegistry(inboxMessageTypes);
    }

    /// <summary>
    /// Validates that UseInbox() is not used on publish channels.
    /// </summary>
    private void ValidatePublishChannels()
    {
        foreach (var channel in channelRegistry.GetPublishChannels())
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
    }

    /// <summary>
    /// Populates <see cref="InboxRoutingTable"/> from consume channel configuration.
    /// Returns the set of CLR types that are inbox-managed.
    /// </summary>
    private HashSet<Type> PopulateRoutingTable()
    {
        var inboxMessageTypes = new HashSet<Type>();

        foreach (var channel in channelRegistry.GetConsumeChannels())
        {
            var channelInboxOpts = channel.GetExtension<InboxChannelOptions>();
            var hasInboxMessages = false;

            foreach (var message in channel.Messages)
            {
                var msgOpts = message.GetExtension<InboxMessageOptions>();
                if (msgOpts is { UseInbox: true })
                {
                    routingTable.RegisterMessage(channel.ChannelName, message.MessageTypeName);
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

            routingTable.RegisterChannel(channel.ChannelName, dbContextType);

            // Validate that the DbContext was explicitly configured via UseEfCoreInbox<T>()
            if (!registeredDbContextTypes.Contains(dbContextType))
            {
                throw new InvalidOperationException(
                    $"Channel '{channel.ChannelName}' uses UseInbox<{dbContextType.Name}>() " +
                    $"but UseEfCoreInbox<{dbContextType.Name}>() was not called. " +
                    $"Add bus.UseEfCoreInbox<{dbContextType.Name}>() to your configuration.");
            }
        }

        return inboxMessageTypes;
    }

    /// <summary>
    /// Registers <see cref="IHostedService"/> entries for inbox and cleanup processors.
    /// </summary>
    private void RegisterHostedServices(IServiceCollection services)
    {
        foreach (var dbContextType in registeredDbContextTypes)
        {
            if (registerBackgroundService.GetValueOrDefault(dbContextType, true))
            {
                var bgProcessorType = typeof(InboxProcessor<>).MakeGenericType(dbContextType);
                services.AddSingleton(typeof(IHostedService), sp => sp.GetRequiredService(bgProcessorType));
            }

            // Register cleanup processor as hosted service if any retention is configured
            var opts = dbContextRegistry.GetOptions(dbContextType);
            if (opts.CompletedRetention != null || opts.PoisonedRetention != null)
            {
                var cleanupType = typeof(InboxCleanupProcessor<>).MakeGenericType(dbContextType);
                services.AddSingleton(typeof(IHostedService), sp => sp.GetRequiredService(cleanupType));
            }
        }
    }

    /// <summary>
    /// Populates <see cref="InboxHandlerRegistry"/> with all handlers for inbox-managed messages.
    /// </summary>
    private void PopulateHandlerRegistry(HashSet<Type> inboxMessageTypes)
    {
        foreach (var pending in pendingHandlers)
        {
            if (!inboxMessageTypes.Contains(pending.MessageType))
                continue;

            var key = pending.Key
                ?? throw new InvalidOperationException(
                    $"Inbox handler '{pending.HandlerType.Name}' does not have a stable key. " +
                    $"Add [HandlerKey(\"...\")] to the handler class or pass a key to " +
                    $"AddHandler<{pending.MessageType.Name}, {pending.HandlerType.Name}>(\"...\").");
            var wireTypeName = InboxPublicApiExtensions.ResolveWireTypeName(channelRegistry, pending.MessageType);
            handlerRegistry.Register(key, pending.MessageType, pending.HandlerType, wireTypeName);
        }
    }
}
