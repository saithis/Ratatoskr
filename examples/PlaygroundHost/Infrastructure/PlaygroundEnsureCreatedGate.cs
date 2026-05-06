namespace PlaygroundHost.Infrastructure;

/// <summary>
/// Serializes EF Core <c>Database.EnsureCreated</c> when multiple in-process hosts start concurrently (for example
/// parallel <c>WebApplicationFactory.CreateClient()</c> before the first host finishes bootstrapping).
/// </summary>
internal static class PlaygroundEnsureCreatedGate
{
    internal static readonly SemaphoreSlim Semaphore = new(1, 1);
}
