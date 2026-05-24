using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Ratatoskr.AsyncApi.Attributes;

namespace Ratatoskr.Tests.AsyncApi;

// A publish message with attribute-driven metadata
[RatatoskrMessage("api-key.revoked")]
[AsyncApiMessage(
    Version = "1.0.0",
    Title = "API Key Revoked",
    Description = "An API key has been revoked."
)]
public record ApiKeyRevokedEvent
{
    [Required]
    public Guid ApiKeyId { get; init; }

    [Required]
    [MaxLength(50)]
    public string Type { get; init; } = "";

    [MaxLength(100)]
    public string? UserId { get; init; }

    [Required]
    public string TenantId { get; init; } = "";

    public DateTimeOffset RevokedAt { get; init; }

    public List<string?> Scopes { get; init; } = [];
}

// A consume message with option-driven metadata (no attribute)
[RatatoskrMessage("user-roles-changed")]
public record UserRolesChangedEvent
{
    [Required]
    public string UserId { get; init; } = "";

    public List<RoleAssignment> AddedRoles { get; init; } = [];
    public List<RoleAssignment> RemovedRoles { get; init; } = [];
}

public record RoleAssignment
{
    [Required]
    [MaxLength(100)]
    public string RoleInternalName { get; init; } = "";

    [Required]
    [JsonPropertyName("assignmentLevel")]
    public AssignmentLevel Level { get; init; }

    public string? TargetId { get; init; }
}

public enum AssignmentLevel
{
    Global,
    Tenant,
}
