using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.Core;

namespace PlaygroundHost.Infrastructure;

internal static class PlaygroundMessageSenderDecoration
{
    public static void WrapAllMessageSenders(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(IMessageSender)).ToList();
        foreach (var d in descriptors)
            services.Remove(d);

        foreach (var original in descriptors)
        {
            services.AddSingleton<IMessageSender>(sp =>
            {
                var inner = CreateInner(sp, original);
                var registry = sp.GetRequiredService<OutboxSendFailureRegistry>();
                return new FailableMessageSender(inner, registry);
            });
        }
    }

    private static IMessageSender CreateInner(IServiceProvider sp, ServiceDescriptor sd)
    {
        if (sd.ImplementationInstance is IMessageSender direct)
            return direct;
        if (sd.ImplementationFactory is { } factory)
            return (IMessageSender)factory(sp);
        if (sd.ImplementationType is { } type)
            return (IMessageSender)ActivatorUtilities.CreateInstance(sp, type);

        throw new InvalidOperationException($"Cannot materialize IMessageSender from {sd}.");
    }
}
