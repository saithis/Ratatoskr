namespace Ratatoskr.EfCore;

/// <summary>
/// Stored as a channel extension on consume channels that have <c>UseInbox&lt;TDbContext&gt;()</c> configured.
/// </summary>
public record ChannelInboxConfig(Type DbContextType);
