using System.Diagnostics.CodeAnalysis;

namespace Ratatoskr.RabbitMq.Management;

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "DTO")]
internal record RabbitMqHealthDto(bool IsConnected, bool IsHealthy, string? ConnectionError);
