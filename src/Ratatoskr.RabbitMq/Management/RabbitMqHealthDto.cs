namespace Ratatoskr.RabbitMq.Management;

internal record RabbitMqHealthDto(bool IsConnected, bool IsHealthy, string? ConnectionError);
