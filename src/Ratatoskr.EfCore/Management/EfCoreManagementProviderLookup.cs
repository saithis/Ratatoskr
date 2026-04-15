namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Lookup for registered <see cref="IEfCoreManagementDbContextProvider"/> instances by DbContext name.
/// Injected into per-context management endpoint handlers.
/// </summary>
internal sealed class EfCoreManagementProviderLookup
{
    private readonly Dictionary<string, IEfCoreManagementDbContextProvider> _byName;

    public EfCoreManagementProviderLookup(IEnumerable<IEfCoreManagementDbContextProvider> providers)
    {
        _byName = providers.ToDictionary(p => p.DbContextName, StringComparer.OrdinalIgnoreCase);
    }

    public IEfCoreManagementDbContextProvider? Find(string contextName) =>
        _byName.GetValueOrDefault(contextName);

    public IReadOnlyCollection<IEfCoreManagementDbContextProvider> All => _byName.Values;
}
