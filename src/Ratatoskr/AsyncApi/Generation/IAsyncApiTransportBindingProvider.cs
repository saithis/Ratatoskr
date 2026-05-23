using Ratatoskr.AsyncApi.Model;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Generation;

/// <summary>
/// Pluggable transport binding provider that adds protocol-specific details to the AsyncAPI document.
/// Implement this interface to support additional transports beyond RabbitMQ.
/// </summary>
public interface IAsyncApiTransportBindingProvider
{
    /// <summary>
    /// Called once during document generation to add server definitions and any top-level transport config.
    /// </summary>
    void ConfigureServers(AsyncApiDocument document, IEnumerable<ChannelRegistration> channels);

    /// <summary>
    /// Called for each channel to add transport-specific channel bindings (e.g. AMQP exchange/queue).
    /// May also add additional channels (e.g. subscription queue channel for consume channels).
    /// </summary>
    void ConfigureChannel(ChannelRegistration channel, AsyncApiDocument document);

    /// <summary>
    /// Called for each operation to add transport-specific operation bindings.
    /// </summary>
    void ConfigureOperation(ChannelRegistration channel, AsyncApiOperation operation);

    /// <summary>
    /// Called for each message to add transport-specific message bindings.
    /// </summary>
    void ConfigureMessage(
        MessageRegistration message,
        ChannelRegistration channel,
        AsyncApiMessage asyncApiMessage
    );
}
