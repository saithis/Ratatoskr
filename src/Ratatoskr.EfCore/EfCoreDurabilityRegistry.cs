using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore;

/// <summary>
/// Describes one DbContext that <c>AddEfCoreDurability&lt;TDbContext&gt;</c> registered.
/// </summary>
/// <remarks>
/// Exposed publicly so packages layered on top of the durability model — the management API,
/// diagnostics, a future reporting tool — can enumerate what is configured without reaching into
/// the service collection or knowing the concrete context types at compile time.
/// </remarks>
public interface IEfCoreDurabilityDescriptor
{
    /// <summary>The short type name, which is what appears in management URLs and in the UI.</summary>
    string Name { get; }

    /// <summary>The full CLR type name, used when a short name collides.</summary>
    string FullName { get; }

    /// <summary>The DbContext type.</summary>
    Type DbContextType { get; }

    /// <summary>Whether an outbox is configured for this context.</summary>
    bool HasOutbox { get; }

    /// <summary>Whether an inbox is configured for this context.</summary>
    bool HasInbox { get; }

    /// <summary>When the outbox processor last completed a pass, if one is running.</summary>
    DateTimeOffset? LastOutboxProcessingAt { get; }

    /// <summary>When the inbox processor last completed a pass, if one is running.</summary>
    DateTimeOffset? LastInboxProcessingAt { get; }

    /// <summary>Resolves the typed DbContext from a scope.</summary>
    DbContext Resolve(IServiceProvider scopedServiceProvider);
}

/// <summary>
/// Every DbContext registered for durability, indexed by short name.
/// </summary>
/// <remarks>
/// Short names are the routing key for per-context APIs, so two contexts that share one would
/// silently collapse into whichever descriptor happened to win. That is caught here, at startup,
/// with both full type names in the message, rather than showing up later as one context's data
/// appearing under another's URL.
/// </remarks>
public sealed class EfCoreDurabilityRegistry
{
    private readonly Dictionary<string, IEfCoreDurabilityDescriptor> _byName;

    /// <summary>Builds the registry and rejects duplicate short names.</summary>
    public EfCoreDurabilityRegistry(IEnumerable<IEfCoreDurabilityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        _byName = new Dictionary<string, IEfCoreDurabilityDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            if (!_byName.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidOperationException(
                    $"Multiple DbContexts share the short name '{descriptor.Name}': "
                        + $"'{_byName[descriptor.Name].FullName}' and '{descriptor.FullName}'. "
                        + "Rename one of them so management API URLs stay unambiguous."
                );
            }
        }
    }

    /// <summary>Every registered context, ordered by name so callers render a stable list.</summary>
    public IReadOnlyList<IEfCoreDurabilityDescriptor> All =>
        [.. _byName.Values.OrderBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Returns the descriptor for <paramref name="name"/>, or null.</summary>
    public IEfCoreDurabilityDescriptor? Find(string name) => _byName.GetValueOrDefault(name);
}

internal sealed class EfCoreDurabilityDescriptor<TDbContext>(IServiceProvider serviceProvider)
    : IEfCoreDurabilityDescriptor
    where TDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    private readonly OutboxProcessor<TDbContext>? _outboxProcessor =
        serviceProvider.GetService<OutboxProcessor<TDbContext>>();
    private readonly InboxProcessor<TDbContext>? _inboxProcessor =
        serviceProvider.GetService<InboxProcessor<TDbContext>>();

    public Type DbContextType { get; } = typeof(TDbContext);

    public string Name => DbContextType.Name;

    public string FullName => DbContextType.FullName ?? DbContextType.Name;

    public bool HasOutbox { get; } =
        serviceProvider.GetService<OutboxOptionsHolder<TDbContext>>() is not null;

    public bool HasInbox { get; } =
        serviceProvider.GetService<InboxOptionsHolder<TDbContext>>() is not null;

    public DateTimeOffset? LastOutboxProcessingAt => _outboxProcessor?.LastSuccessfulProcessingAt;

    public DateTimeOffset? LastInboxProcessingAt => _inboxProcessor?.LastSuccessfulProcessingAt;

    public DbContext Resolve(IServiceProvider scopedServiceProvider)
    {
        ArgumentNullException.ThrowIfNull(scopedServiceProvider);
        return scopedServiceProvider.GetRequiredService<TDbContext>();
    }
}
