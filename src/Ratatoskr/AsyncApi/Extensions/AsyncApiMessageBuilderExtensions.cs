using Ratatoskr.AsyncApi.Config;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Extensions;

public static class AsyncApiMessageBuilderExtensions
{
    public static MessageBuilder WithAsyncApi(
        this MessageBuilder builder,
        Action<AsyncApiMessageOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        var opts = new AsyncApiMessageOptions();
        configure(opts);
        builder.MessageRegistration.SetExtension(opts);
        return builder;
    }

    internal static AsyncApiMessageOptions? GetAsyncApiMessageOptions(
        this MessageRegistration registration
    ) => registration.GetExtension<AsyncApiMessageOptions>();
}
