namespace Ratatoskr.Core;

public interface ITransportMessageMetadataEnricher
{
    string TransportName { get; }
    void Enrich(PublishInformation publishInformation, MessageProperties properties);
}
