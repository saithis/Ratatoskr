namespace Ratatoskr.UI.Proxy;

/// <summary>
/// Singleton bridge that carries the <see cref="RatatoskrUiOptions"/> configured in
/// <c>UseRatatoskrUi</c> into <c>MapRatatoskrUiRoutes</c> without requiring the caller
/// to thread the options object through manually.
/// </summary>
internal sealed class RatatoskrUiOptionsHolder
{
    internal RatatoskrUiOptions? Options { get; set; }
}
