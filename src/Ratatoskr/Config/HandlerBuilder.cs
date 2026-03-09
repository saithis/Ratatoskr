using Ratatoskr.Core;

namespace Ratatoskr.Config;

public class HandlerBuilder(HandlerRegistration registration)
{
    internal HandlerRegistration Registration => registration;

    /// <summary>
    /// Sets a typed extension on the handler registration.
    /// Used by infrastructure packages to attach handler-specific configuration.
    /// </summary>
    protected internal HandlerBuilder WithExtension<T>(T value) where T : class
    {
        registration.SetExtension(value);
        return this;
    }
}
