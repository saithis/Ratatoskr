using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Lookup for registered <see cref="IEfCoreManagementDbContextDescriptor"/> instances by DbContext name.
/// Injected into per-context management endpoint handlers.
/// </summary>
/// <remarks>
/// The lookup key is the short type name (<c>typeof(TDbContext).Name</c>) because that is what
/// surfaces in URLs and in the UI. Short names must be unique for routing, which we enforce below.
/// </remarks>
internal sealed class EfCoreManagementDbContextLookup
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, IEfCoreManagementDbContextDescriptor> _byName;

    public EfCoreManagementDbContextLookup(IEnumerable<IEfCoreManagementDbContextDescriptor> providers, IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _byName = new Dictionary<string, IEfCoreManagementDbContextDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!_byName.TryAdd(provider.DbContextName, provider))
            {
                // Duplicate short names would silently collapse into one entry and make the
                // management API respond for whichever provider won the race. Surface the
                // conflict at startup with the full CLR names so the operator can rename
                // one of the DbContext types (or wrap the offending type in a distinct alias).
                var existing = _byName[provider.DbContextName];
                throw new InvalidOperationException(
                    $"Multiple DbContexts share the short name '{provider.DbContextName}': " +
                    $"'{existing.DbContextFullName}' and '{provider.DbContextFullName}'. " +
                    "Rename one of them so management API URLs stay unambiguous.");
            }
        }
    }

    public DbContext? GetDbContext(string contextName)
    {
        var descriptor = Find(contextName);
        if (descriptor is null) return null;
        return (DbContext?)_serviceProvider.GetService(descriptor.DbContextType);
    }
    
    public IEfCoreManagementDbContextDescriptor? Find(string contextName) =>
        _byName.GetValueOrDefault(contextName);

    public IReadOnlyCollection<IEfCoreManagementDbContextDescriptor> All => _byName.Values;
}
