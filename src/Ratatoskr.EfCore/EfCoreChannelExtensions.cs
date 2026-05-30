using Ratatoskr.Config;

namespace Ratatoskr.EfCore;

/// <summary>
/// Extension methods for configuring EF Core as a publish channel transport.
/// </summary>
public static class EfCoreChannelExtensions
{
    extension(PublishChannelBuilder builder)
    {
        /// <summary>
        /// Configures this publish channel to deliver messages via the EF Core transport.
        /// Messages are written directly to the inbox tables for durable in-process delivery.
        /// </summary>
        public PublishChannelBuilder WithEfCore()
        {
            builder.AddTransport(EfCoreTransportConstants.TransportName);
            return builder;
        }
    }
}
