using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.Core;

namespace Ratatoskr.Local;

public static class LocalTransportExtensions
{
    extension(RatatoskrBuilder builder)
    {
        /// <summary>
        /// Enables the local in-process transport.
        /// Messages published to channels configured with <c>.WithLocal()</c> will be
        /// delivered to handlers in the same process without blocking the sender.
        /// </summary>
        public RatatoskrBuilder UseLocalTransport(Action<LocalTransportOptions>? configure = null)
        {
            var options = new LocalTransportOptions();
            configure?.Invoke(options);

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<LocalTelemetry>();

            builder.Services.AddSingleton(_ =>
                Channel.CreateBounded<LocalMessage>(
                    new BoundedChannelOptions(options.ChannelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = false,
                    }));

            // Sending
            builder.Services.AddSingleton<ITransportMessageMetadataEnricher, LocalTransportMetadataEnricher>();
            builder.Services.AddSingleton<IMessageSender, LocalMessageSender>();

            // Consuming
            builder.Services.TryAddSingleton<MessageDispatcher>();
            builder.Services.AddHostedService<LocalTransportConsumer>();

            return builder;
        }
    }
}
