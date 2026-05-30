using Ratatoskr.AsyncApi.Config;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Extensions;

/// <summary>
/// Extension methods for configuring AsyncAPI documentation on channel builders.
/// </summary>
public static class AsyncApiChannelBuilderExtensions
{
    /// <summary>Attaches AsyncAPI channel-level documentation options to a publish channel builder.</summary>
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

    /// <summary>Attaches AsyncAPI channel-level documentation options to a consume channel builder.</summary>
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
