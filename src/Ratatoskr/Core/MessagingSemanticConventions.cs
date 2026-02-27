namespace Ratatoskr.Core;

/// <summary>
/// OpenTelemetry semantic convention constants for messaging.
/// See: https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/
/// See: https://opentelemetry.io/docs/specs/semconv/messaging/rabbitmq/
/// See: https://opentelemetry.io/docs/specs/semconv/messaging/messaging-metrics/
/// </summary>
internal static class MessagingSemanticConventions
{
    // Required attributes
    public const string System = "messaging.system";
    public const string OperationName = "messaging.operation.name";
    public const string OperationType = "messaging.operation.type";

    // Destination attributes
    public const string DestinationName = "messaging.destination.name";
    public const string DestinationSubscriptionName = "messaging.destination.subscription.name";

    // Message attributes
    public const string MessageId = "messaging.message.id";
    public const string MessageBodySize = "messaging.message.body.size";

    // Server attributes
    public const string ServerAddress = "server.address";
    public const string ServerPort = "server.port";

    // Error attributes
    public const string ErrorType = "error.type";

    // RabbitMQ-specific attributes
    public const string RabbitMqRoutingKey = "messaging.rabbitmq.destination.routing_key";
    public const string RabbitMqDeliveryTag = "messaging.rabbitmq.message.delivery_tag";

    // Operation type values
    public const string OperationTypeCreate = "create";
    public const string OperationTypeSend = "send";
    public const string OperationTypeProcess = "process";
}
