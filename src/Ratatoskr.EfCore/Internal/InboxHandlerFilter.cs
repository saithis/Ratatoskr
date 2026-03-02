using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Implements <see cref="IHandlerFilter"/> by checking the <see cref="InboxHandlerRegistry"/>
/// to skip handlers that are managed by the inbox processor.
/// </summary>
internal class InboxHandlerFilter(InboxHandlerRegistry registry) : IHandlerFilter
{
    public bool ShouldSkip(Type handlerType) =>
        registry.GetByHandlerType(handlerType) != null;
}
