namespace Ratatoskr.Core;

/// <summary>
/// Enriches outgoing message properties with transport-specific metadata before sending.
/// </summary>
public interface ITransportMessageMetadataEnricher
{
    /// <summary>The name of the transport this enricher applies to.</summary>
    public string TransportName { get; }

    /// <summary>Populates transport-specific fields on the message properties.</summary>
    public void Enrich(PublishInformation publishInformation, MessageProperties properties);
}
