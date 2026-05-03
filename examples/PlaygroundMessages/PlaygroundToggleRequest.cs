namespace PlaygroundMessages;

/// <param name="Key">Toggle key from control-state.</param>
/// <param name="Mode">Optional: <c>succeed</c>, <c>fail</c>, <c>succeed-after</c>. When null, services cycle the toggle.</param>
/// <param name="FailureCount">Used with <c>succeed-after</c>: number of failures before success.</param>
public sealed record PlaygroundToggleRequest(string Key, string? Mode = null, int? FailureCount = null);
