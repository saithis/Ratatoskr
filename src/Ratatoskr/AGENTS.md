# Core Library (Ratatoskr)

This project contains the fundamental abstractions and core logic for the Ratatoskr library.

## Key Abstractions

- **`IRatatoskr`**: The primary interface for publishing events. Developers use this interface to send messages.
- **`MessageProperties`**: Holds additional message properties like headers, content type, etc. It is used by the transports to send the message.
- **`CloudEventEnvelope`**: The generic wrapper for messages that use the structured mode. It encapsulates the payload and standard CloudEvent attributes (Id, Source, Type, etc.). For binary mode (default) the payload is the message itself and the CloudEvents attributes are set as headers.
- **`IMessageHandler<T>`**: (Pattern) The library supports multiple handlers for the same message type.

## Extension Points

- **`RatatoskrBuilder`**: Used to configure the bus, register transports, and add middlewares.
- **Serialization**: The library supports pluggable serializers. The default serializer should be sufficient for most JSON-based payloads.

## Development Guidelines

- **Transport independence**: This project *must not* depend on RabbitMQ or other message brokers. EF Core is the one exception: the outbox, inbox and EF Core transport live in `EfCore/`.
- **Attributes**: Use attributes like `[RatatoskrMessage]` to decorate message classes for auto-discovery or metadata.
- **Extensible**: Provide extension points for custom implementations of transports, serializers, and middlewares. But provide as much of the logic in a generic way as possible.
