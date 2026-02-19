using Ratatoskr.AsyncApi.Config;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Extensions;

public static class AsyncApiChannelBuilderExtensions
{
    private const string MetadataKey = "AsyncApiChannelOptions";

    public static ChannelBuilder WithAsyncApi(this ChannelBuilder builder, Action<AsyncApiChannelOptions> configure)
    {
        var opts = new AsyncApiChannelOptions();
        configure(opts);
        builder.WithMetadata(MetadataKey, opts);
        return builder;
    }

    internal static AsyncApiChannelOptions? GetAsyncApiChannelOptions(this ChannelRegistration registration)
        => registration.Metadata.GetValueOrDefault(MetadataKey) as AsyncApiChannelOptions;
}
