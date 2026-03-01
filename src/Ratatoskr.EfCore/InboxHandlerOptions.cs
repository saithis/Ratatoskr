namespace Ratatoskr.EfCore;

/// <summary>
/// Extension data attached to a <see cref="Ratatoskr.Core.HandlerRegistration"/>
/// to control inbox participation for a specific handler.
/// </summary>
internal class InboxHandlerOptions
{
    /// <summary>null = use global default; true = use inbox; false = fire-and-forget.</summary>
    internal bool? UseInboxExplicit { get; init; }

    /// <summary>null = use handler type full name as key.</summary>
    internal string? Key { get; init; }
}
