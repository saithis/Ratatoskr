using Ratatoskr.AsyncApi.Model;

namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// Top-level options for AsyncAPI document generation.
/// </summary>
public sealed class AsyncApiOptions
{
    /// <summary>The Info section of the AsyncAPI document.</summary>
    public AsyncApiInfo Info { get; set; } = new();

    public AsyncApiOptions WithTitle(string title)
    {
        Info.Title = title;
        return this;
    }

    public AsyncApiOptions WithVersion(string version)
    {
        Info.Version = version;
        return this;
    }

    public AsyncApiOptions WithDescription(string description)
    {
        Info.Description = description;
        return this;
    }

    public AsyncApiOptions WithContact(string name, string? url = null, string? email = null)
    {
        Info.Contact = new AsyncApiContact
        {
            Name = name,
            Url = url,
            Email = email,
        };
        return this;
    }

    public AsyncApiOptions WithContact(string name, Uri url = null, string? email = null)
    {
        throw new NotImplementedException();
    }
}
