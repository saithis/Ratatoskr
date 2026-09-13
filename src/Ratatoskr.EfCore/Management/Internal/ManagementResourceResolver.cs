using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ratatoskr.EfCore;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Management.EfCore.Internal;

/// <summary>Which half of the durability model an operation needs.</summary>
internal enum DurabilityFeature
{
    Outbox,
    Inbox,
}

/// <summary>
/// Resolves the DbContext an operation runs against, or explains why it cannot.
/// </summary>
/// <remarks>
/// Scoped, because it hands back a scoped <c>DbContext</c>. The dispatcher creates a scope per
/// command execution, so a singleton broker consumer can never end up holding one of these.
/// </remarks>
internal sealed class ManagementResourceResolver(
    EfCoreDurabilityRegistry registry,
    IServiceProvider scopedServices,
    IOptions<ManagementAgentOptions> options
)
{
    /// <summary>
    /// Resolves the context named by <paramref name="context"/>, applying the management command
    /// timeout to it.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> on success, with <paramref name="dbContext"/> set; otherwise the
    /// failure to return to the caller.
    /// </returns>
    public ManagementResult? Resolve(
        ManagementOperationContext context,
        DurabilityFeature feature,
        out DbContext dbContext
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        dbContext = null!;

        if (string.IsNullOrWhiteSpace(context.Resource))
        {
            return ManagementResult.Invalid(
                $"Operation '{context.Operation}' must name the DbContext it applies to.",
                ManagementErrorCodes.InvalidRequest
            );
        }

        var descriptor = registry.Find(context.Resource);
        if (descriptor is null)
        {
            return ManagementResult.NotFound(
                $"No DbContext is registered under the name '{context.Resource}'."
            );
        }

        var supported = feature switch
        {
            DurabilityFeature.Outbox => descriptor.HasOutbox,
            DurabilityFeature.Inbox => descriptor.HasInbox,
            _ => false,
        };

        if (!supported)
        {
            return ManagementResult.NotFound(
                $"DbContext '{descriptor.Name}' has no {feature.ToString().ToLowerInvariant()} configured.",
                ManagementErrorCodes.UnsupportedCapability
            );
        }

        dbContext = descriptor.Resolve(scopedServices);

        // Bound every management query below the protocol deadline. Without this a substring
        // search over a large retained table holds a connection long after the caller gave up.
        dbContext.Database.SetCommandTimeout(options.Value.QueryTimeout);
        return null;
    }

    /// <summary>Resolves a context without requiring either half of the model.</summary>
    public ManagementResult? ResolveAny(
        ManagementOperationContext context,
        out IEfCoreDurabilityDescriptor descriptor,
        out DbContext dbContext
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        descriptor = null!;
        dbContext = null!;

        if (string.IsNullOrWhiteSpace(context.Resource))
        {
            return ManagementResult.Invalid(
                $"Operation '{context.Operation}' must name the DbContext it applies to."
            );
        }

        var found = registry.Find(context.Resource);
        if (found is null)
        {
            return ManagementResult.NotFound(
                $"No DbContext is registered under the name '{context.Resource}'."
            );
        }

        descriptor = found;
        dbContext = found.Resolve(scopedServices);
        dbContext.Database.SetCommandTimeout(options.Value.QueryTimeout);
        return null;
    }

    /// <summary>Every registered context.</summary>
    public IReadOnlyList<IEfCoreDurabilityDescriptor> All => registry.All;
}
