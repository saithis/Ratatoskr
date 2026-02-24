using Ratatoskr.Config;

namespace Ratatoskr.Local;

public static class LocalChannelExtensions
{
    extension(PublishChannelBuilder builder)
    {
        /// <summary>
        /// Configures this publish channel to also deliver messages via the local in-process transport.
        /// </summary>
        public PublishChannelBuilder WithLocal()
        {
            builder.AddTransport("local");
            return builder;
        }
    }
}
