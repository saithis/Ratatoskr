namespace PlaygroundMessages;

/// <summary>Dev-only handler outcome for playground toggles.</summary>
public enum PlaygroundOutcomeMode
{
    /// <summary>Handler succeeds on every invocation.</summary>
    Succeed,

    /// <summary>Handler throws on every invocation.</summary>
    AlwaysFail,

    /// <summary>Handler throws until <see cref="PlaygroundToggleRequest.FailureCount"/> failures are consumed, then succeeds.</summary>
    SucceedAfterNFailures,
}
