# Ratatoskr

## Goals

- Exceptional developer experience in combination with RabbitMq and EfCore
- Abstractions to make other transports like Kafka possible
- By default send messages as CloudEvents, but support other formats
- Very good error handling and recovery
- Works with horizontally scaled applications
- Good Observability
- Easy to test

## Ratatoskr:

- Generic implementation of event/message sending/receiving and asyncapi
- Support for different message serializers with a default one and overwritable per message
- Support for multiple handlers of the same message

## Ratatoskr.RabbitMq:

- RabbitMq implementations for sending, receiving and CloudEvents mapping
- RabbitMq specific asyncapi bindings

## Ratatoskr.EfCore:

- Implements the Outbox pattern via EfCore 
- Exceptional developer experience
- Low latency
- Low resource usage
