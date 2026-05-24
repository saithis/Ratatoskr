using Ratatoskr.AsyncApi.Config;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Extensions;

public static class AsyncApiChannelBuilderExtensions
{
    public static PublishChannelBuilder WithAsyncApi(
        this PublishChannelBuilder builder,
        Action<AsyncApiChannelOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var opts = new AsyncApiChannelOptions();
        configure(opts);
        _ = builder.WithExtension(opts);
        return builder;
    }

    public static ConsumeChannelBuilder WithAsyncApi(
        this ConsumeChannelBuilder builder,
        Action<AsyncApiChannelOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var opts = new AsyncApiChannelOptions();
        configure(opts);
        _ = builder.WithExtension(opts);
        return builder;
    }

    internal static AsyncApiChannelOptions? GetAsyncApiChannelOptions(
        this ChannelRegistration registration
    ) => registration.GetExtension<AsyncApiChannelOptions>();
}
