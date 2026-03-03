namespace Ratatoskr.EfCore;

/// <summary>
/// Extension data stored on a <see cref="Ratatoskr.Core.ChannelRegistration"/>
/// to record which DbContext type the inbox uses for that consume channel.
/// Set by <c>ConsumeChannelBuilder.UseInbox&lt;TDbContext&gt;()</c>.
/// </summary>
internal class InboxChannelOptions(Type dbContextType)
{
    public Type DbContextType { get; } = dbContextType;
}
