namespace Ratatoskr.Core;

public interface ITransportMessageMetadataEnricher
{
    public string TransportName { get; }
    public void Enrich(PublishInformation publishInformation, MessageProperties properties);
}
