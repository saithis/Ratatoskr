using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;

namespace Ratatoskr.Testing;

public static class InMemoryRatatoskrExtensions
{
    /// <summary>
    /// Configures Ratatoskr to use an in-memory test harness instead of a real message broker.
    /// This registers <see cref="RatatoskrTestHarness"/>, <see cref="MessageSink"/>, and core dispatching components.
    /// </summary>
    /// <param name="builder">The Ratatoskr builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static RatatoskrBuilder UseInMemory(this RatatoskrBuilder builder)
    {
        // Register core testing components
        builder.Services.AddSingleton<MessageSink>(sp => new MessageSink
        {
            Registry = sp.GetRequiredService<ChannelRegistry>()
        });
        builder.Services.AddSingleton<IMessageSender>(sp => sp.GetRequiredService<MessageSink>());

        // Register MessageDispatcher (required for processing simulated incoming messages)
        builder.Services.AddSingleton<MessageDispatcher>();

        // Register the harness itself
        builder.Services.AddSingleton<RatatoskrTestHarness>();

        // Register default metadata enricher for in-memory transport
        builder.Services.AddSingleton<ITransportMessageMetadataEnricher, InMemoryTransportMessageMetadataEnricher>();

        return builder;
    }
}

internal class InMemoryTransportMessageMetadataEnricher : ITransportMessageMetadataEnricher
{
    public void Enrich(PublishInformation publishInformation, MessageProperties properties)
    {
        // No-op for in-memory transport
    }
}
