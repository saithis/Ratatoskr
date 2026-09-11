using Microsoft.Extensions.Options;

namespace Ratatoskr.Management.Runtime;

/// <summary>Options shared by transport-neutral management runtime providers.</summary>
public sealed class ManagementRuntimeOptions
{
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

internal sealed class ManagementRuntimeOptionsValidator : IValidateOptions<ManagementRuntimeOptions>
{
    public ValidateOptionsResult Validate(string? name, ManagementRuntimeOptions options) =>
        options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(5)
            ? ValidateOptionsResult.Fail("Management request timeout must be greater than zero and no more than five minutes.")
            : ValidateOptionsResult.Success;
}
