# RabbitMQ Transport (Ratatoskr.RabbitMq)

This project implements the transport layer using RabbitMQ.

## Key Components

- **`RabbitMqTopologyManager`**: Responsible for declaring exchanges, queues, and bindings. It ensures the broker topology matches the code configuration.
- **`RabbitMqMessageSender`**: Implements the actual sending logic to RabbitMQ exchanges.
- **`RabbitMqConsumer`**: Listens to queues and dispatches messages to the appropriate handlers in the core library.

## Retry Mechanism

- **`RabbitMqRetryHandler`**: Implements the retry logic. It uses Dead Letter Exchanges (DLX) or delayed exchanges to handle retries without blocking the main processing threads.
- See `RabbitMqChannelOptions.Retry` (`WithRetry(...)`) for retry configuration (counts, backoff intervals).

## Configuration

- **`RabbitMqOptions`**: The main configuration object.
- **Mapping**: `IRabbitMqEnvelopeMapper` handles the conversion between `MessageProperties` and RabbitMQ's `IBasicProperties` + body.

## Important Notes

- **Channel Management**: Be careful with channel usage. Channels are not thread-safe.
- **Connection**: `RabbitMqConnectionManager` handles the lifecycle of the persistent connection.
- **Validation**: Channel configuration rules (queue names, quorum constraints, concurrency vs. prefetch) belong in `RabbitMqConfigurationValidator`, which runs at startup.
- **Headers**: The messages should be send with standard rabbitmq headers and the ones defined in the CloudEvents AMQP protocol binding. See: https://github.com/cloudevents/spec/blob/main/cloudevents/bindings/amqp-protocol-binding.md. No custom headers should be added.
