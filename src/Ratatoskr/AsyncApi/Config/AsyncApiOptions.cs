using Ratatoskr.AsyncApi.Model;

namespace Ratatoskr.AsyncApi.Config;

/// <summary>
/// Top-level options for AsyncAPI document generation.
/// </summary>
public sealed class AsyncApiOptions
{
    /// <summary>The Info section of the AsyncAPI document.</summary>
    public AsyncApiInfo Info { get; set; } = new();

    /// <summary>Sets the title of the AsyncAPI document.</summary>
    public AsyncApiOptions WithTitle(string title)
    {
        Info.Title = title;
        return this;
    }

    /// <summary>Sets the version of the AsyncAPI document.</summary>
    public AsyncApiOptions WithVersion(string version)
    {
        Info.Version = version;
        return this;
    }

    /// <summary>Sets the description of the AsyncAPI document.</summary>
    public AsyncApiOptions WithDescription(string description)
    {
        Info.Description = description;
        return this;
    }

    /// <summary>Sets the contact information for the AsyncAPI document using a URL string.</summary>
    public AsyncApiOptions WithContact(string name, string? url = null, string? email = null)
    {
        Info.Contact = new AsyncApiContact
        {
            Name = name,
            Url = url is null ? null : new Uri(url),
            Email = email,
        };
        return this;
    }

    /// <summary>Sets the contact information for the AsyncAPI document using a <see cref="Uri"/>.</summary>
    public AsyncApiOptions WithContact(string name, Uri? url = null, string? email = null)
    {
        Info.Contact = new AsyncApiContact
        {
            Name = name,
            Url = url,
            Email = email,
        };
        return this;
    }
}
