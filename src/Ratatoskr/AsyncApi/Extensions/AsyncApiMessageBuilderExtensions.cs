using Ratatoskr.AsyncApi.Config;
using Ratatoskr.Config;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Extensions;

/// <summary>
/// Extension methods for configuring AsyncAPI documentation on message builders.
/// </summary>
public static class AsyncApiMessageBuilderExtensions
{
    /// <summary>Attaches AsyncAPI message-level documentation options to a message builder.</summary>
    public static MessageBuilder WithAsyncApi(
        this MessageBuilder builder,
        Action<AsyncApiMessageOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
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
