namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Lookup for registered <see cref="IEfCoreManagementDbContextProvider"/> instances by DbContext name.
/// Injected into per-context management endpoint handlers.
/// </summary>
/// <remarks>
/// The lookup key is the short type name (<c>typeof(TDbContext).Name</c>) because that is what
/// surfaces in URLs and in the UI. Metric state inside each provider is keyed on the full
/// type name (<see cref="IEfCoreManagementDbContextProvider.MetricsContextKey"/>) so that two
/// distinct types with the same short name can still report distinct metrics — we only need
/// short names to be unique for routing, which we enforce below.
/// </remarks>
internal sealed class EfCoreManagementProviderLookup
{
    private readonly Dictionary<string, IEfCoreManagementDbContextProvider> _byName;

    public EfCoreManagementProviderLookup(IEnumerable<IEfCoreManagementDbContextProvider> providers)
    {
        _byName = new Dictionary<string, IEfCoreManagementDbContextProvider>(StringComparer.OrdinalIgnoreCase);
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
                    $"'{existing.MetricsContextKey}' and '{provider.MetricsContextKey}'. " +
                    "Rename one of them so management API URLs stay unambiguous.");
            }
        }
    }

    public IEfCoreManagementDbContextProvider? Find(string contextName) =>
        _byName.GetValueOrDefault(contextName);

    public IReadOnlyCollection<IEfCoreManagementDbContextProvider> All => _byName.Values;
}
