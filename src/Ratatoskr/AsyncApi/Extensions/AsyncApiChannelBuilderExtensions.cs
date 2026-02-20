using Ratatoskr.AsyncApi.Config;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Extensions;

public static class AsyncApiChannelBuilderExtensions
{
    public static ChannelBuilder WithAsyncApi(this ChannelBuilder builder, Action<AsyncApiChannelOptions> configure)
    {
        var opts = new AsyncApiChannelOptions();
        configure(opts);
        builder.WithExtension(opts);
        return builder;
    }

    internal static AsyncApiChannelOptions? GetAsyncApiChannelOptions(this ChannelRegistration registration)
        => registration.GetExtension<AsyncApiChannelOptions>();
}
