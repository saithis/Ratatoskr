using Ratatoskr.Core;

namespace Ratatoskr.Local;

internal class LocalTransportMetadataEnricher : ITransportMessageMetadataEnricher
{
    public string TransportName => LocalTransportConstants.TransportName;

    public void Enrich(PublishInformation publishInformation, MessageProperties properties)
    {
        // Local transport doesn't need transport-specific metadata enrichment.
        // No exchange or routing key needed - messages are dispatched in-process.
    }
}
