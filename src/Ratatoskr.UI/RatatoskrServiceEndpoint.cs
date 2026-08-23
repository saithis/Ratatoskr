using System.Diagnostics.CodeAnalysis;

namespace Ratatoskr.UI;

/// <summary>
/// Defines a remote Ratatoskr service endpoint for multi-service dashboard mode.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "DTO for JSON serialization"
)]
public record RatatoskrServiceEndpoint(string Name, string ManagementApiUrl)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RatatoskrServiceEndpoint"/> class using a Uri.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1054:URI-like parameters should not be strings",
        Justification = "Overload provided"
    )]
    public RatatoskrServiceEndpoint(string name, Uri managementApiUrl)
        : this(name, ValidateUri(managementApiUrl)) { }

    private static string ValidateUri(Uri managementApiUrl)
    {
        ArgumentNullException.ThrowIfNull(managementApiUrl);
        return managementApiUrl.ToString();
    }
}
