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

    /// <summary>
    /// Explicitly opts this handler out of deferred (inbox) processing.
    /// The handler will be invoked synchronously (fire-and-forget).
    /// </summary>
    public HandlerBuilder WithoutInbox()
    {
        registration.SetExtension(new DeferredProcessingOverride { OptOut = true });
        return this;
    }
}
