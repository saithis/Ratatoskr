using Microsoft.AspNetCore.Http.HttpResults;
using Ratatoskr.Management;

namespace Ratatoskr.EfCore.Management;

/// <summary>
/// Collapses the "look up a provider by context name and fail with a ProblemDetails
/// NotFound if it is missing or does not support the requested feature" dance that
/// every per-context endpoint would otherwise repeat verbatim.
/// </summary>
/// <remarks>
/// The helpers return <c>null</c> on success and a <see cref="ProblemHttpResult"/> on
/// failure so callers can early-return the error directly — the flow
/// <c>if (Resolver.EnsureOutbox(...) is { } error) return error;</c> keeps handler
/// methods short and stops diverging error copy from creeping back in.
/// </remarks>
internal static class ManagementProviderResolver
{
    internal static ProblemHttpResult? EnsureContext(
        EfCoreManagementProviderLookup lookup,
        string contextName,
        out IEfCoreManagementDbContextProvider provider)
    {
        var found = lookup.Find(contextName);
        if (found is null)
        {
            provider = null!;
            return ManagementResults.NotFound($"No DbContext is registered under name '{contextName}'.");
        }

        provider = found;
        return null;
    }

    internal static ProblemHttpResult? EnsureOutbox(
        EfCoreManagementProviderLookup lookup,
        string contextName,
        out IEfCoreManagementDbContextProvider provider)
    {
        if (EnsureContext(lookup, contextName, out provider) is { } error) return error;
        if (!provider.HasOutbox)
        {
            provider = null!;
            return ManagementResults.NotFound($"No outbox is registered for DbContext '{contextName}'.");
        }
        return null;
    }

    internal static ProblemHttpResult? EnsureInbox(
        EfCoreManagementProviderLookup lookup,
        string contextName,
        out IEfCoreManagementDbContextProvider provider)
    {
        if (EnsureContext(lookup, contextName, out provider) is { } error) return error;
        if (!provider.HasInbox)
        {
            provider = null!;
            return ManagementResults.NotFound($"No inbox is registered for DbContext '{contextName}'.");
        }
        return null;
    }
}
