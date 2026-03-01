namespace Ratatoskr;

/// <summary>
/// Per-handler inbox configuration. Passed to
/// <see cref="RatatoskrBuilder.AddHandler{TMessage,THandler}(Action{HandlerInboxConfig}?)"/>.
/// </summary>
public class HandlerInboxConfig
{
    internal bool? UseInboxExplicit { get; private set; }
    internal string? Key { get; private set; }

    /// <summary>
    /// Routes this handler through the durable inbox with the given stable key.
    /// The key is persisted to the database — it must remain stable across deployments.
    /// </summary>
    public HandlerInboxConfig WithInbox(string stableKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableKey);
        UseInboxExplicit = true;
        Key = stableKey;
        return this;
    }

    /// <summary>
    /// Routes this handler through the durable inbox, using the handler's CLR full name as the stable key.
    /// </summary>
    public HandlerInboxConfig WithInbox()
    {
        UseInboxExplicit = true;
        Key = null;
        return this;
    }

    /// <summary>
    /// Explicitly opts this handler out of the inbox. The handler will be called synchronously (fire-and-forget),
    /// even when a global default inbox is configured.
    /// </summary>
    public HandlerInboxConfig WithoutInbox()
    {
        UseInboxExplicit = false;
        Key = null;
        return this;
    }
}
