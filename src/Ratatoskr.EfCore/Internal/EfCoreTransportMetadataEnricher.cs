using Ratatoskr.Core;

namespace Ratatoskr.EfCore.Internal;

internal class EfCoreTransportMetadataEnricher : ITransportMessageMetadataEnricher
{
    public string TransportName => EfCoreTransportConstants.TransportName;

    public void Enrich(PublishInformation publishInformation, MessageProperties properties)
    {
        // EF Core transport doesn't need transport-specific metadata enrichment.
    }
}
