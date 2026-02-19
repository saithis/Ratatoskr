using Ratatoskr.AsyncApi.Config;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Extensions;

public static class AsyncApiMessageBuilderExtensions
{
    private const string MetadataKey = "AsyncApiMessageOptions";

    public static MessageBuilder WithAsyncApi(this MessageBuilder builder, Action<AsyncApiMessageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var opts = new AsyncApiMessageOptions();
        configure(opts);
        builder.MessageRegistration.Metadata[MetadataKey] = opts;
        return builder;
    }

    internal static AsyncApiMessageOptions? GetAsyncApiMessageOptions(this MessageRegistration registration)
        => registration.Metadata.GetValueOrDefault(MetadataKey) as AsyncApiMessageOptions;
}
