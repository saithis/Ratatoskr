namespace Ratatoskr.EfCore;

/// <summary>
/// Controls whether consume channels are required to opt into <c>UseInbox&lt;TDbContext&gt;()</c>.
/// </summary>
public enum ConsumeChannelInboxRequirement
{
    /// <summary>
    /// No additional validation. Channels without <c>UseInbox&lt;TDbContext&gt;()</c> are allowed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Startup succeeds, but channels without <c>UseInbox&lt;TDbContext&gt;()</c> emit warnings.
    /// </summary>
    Warn = 1,

    /// <summary>
    /// Startup fails when a consume channel does not use <c>UseInbox&lt;TDbContext&gt;()</c>.
    /// </summary>
    Fail = 2,
}
