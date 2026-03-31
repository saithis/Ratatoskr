# CloudEvents

Ratatoskr uses the [CloudEvents](https://cloudevents.io/) specification as its message envelope format. Every message published through Ratatoskr is a CloudEvents v1.0 event with standard attributes for identity, type, source, and trace context. This page covers how CloudEvents maps to Ratatoskr's `MessageProperties`, content modes, and configuration.

## CloudEvents Attributes

When a message is published, Ratatoskr automatically populates these CloudEvents attributes in `MessageProperties`:

| CloudEvents Attribute | `MessageProperties` Property | Source |
|----------------------|------------------------------|--------|
| `id` | `Id` | Auto-generated GUID v7 |
| `source` | `Source` | `ConfigureCloudEvents(ce => ce.DefaultSource = "...")` |
| `type` | `Type` | `[RatatoskrMessage("order.placed")]` attribute |
| `specversion` | (always `"1.0"`) | Hardcoded |
| `time` | `Time` | Current UTC timestamp from `TimeProvider` |
| `datacontenttype` | `DataContentType` | `"application/json"` (default) |
| `dataschema` | `DataSchema` | Optional URI identifying the data schema |
| `traceparent` | `TraceParent` | W3C trace context from `Activity.Current` |
| `tracestate` | `TraceState` | W3C trace state from `Activity.Current` |

### Data Schema

The optional `dataschema` attribute identifies the schema that the event data adheres to. When present, it must be a valid URI (per the CloudEvents spec). Set it via `MessageProperties.DataSchema`:

```csharp
var props = new MessageProperties
{
    DataSchema = "https://schemas.example.com/events/order.placed/v1.json",
};
await bus.PublishDirectAsync(orderPlaced, props);
```

The attribute is preserved in both binary mode (as a `cloudEvents_dataschema` header) and structured mode (as the `dataschema` envelope field). Consumers receive it on `MessageProperties.DataSchema`.

> **Note:** Per-message-type default configuration (e.g. via `[RatatoskrMessage]` attribute or a channel-level setting) is not yet supported. `DataSchema` must be set explicitly on each `MessageProperties` instance.

### Extension Attributes

You can attach custom extension attributes via `MessageProperties.Extensions`:

```csharp
var props = new MessageProperties();
props.Extensions["tenantid"] = "acme-corp";
props.Extensions["correlationid"] = correlationId;

await bus.PublishDirectAsync(message, props);
```

Extension attributes are preserved across the entire pipeline — through outbox persistence, transport delivery, and handler invocation.

## Configuration

Configure CloudEvents defaults in the Ratatoskr builder:

[!code-csharp[](../examples/Docs/Program.cs#ConfigureCloudEvents)]

| Property | Default | Description |
|----------|---------|-------------|
| `DefaultSource` | `"/"` | The CloudEvents `source` URI for all messages from this application |
| `ContentMode` | `Binary` | How CloudEvents attributes are encoded on the wire (see below) |

## Content Modes

CloudEvents defines two encoding modes for transport:

### Binary Mode (Default)

CloudEvents attributes are placed in transport headers (e.g., AMQP application properties). The message body contains only the serialized data payload.

```text
AMQP Headers:
  cloudEvents_specversion: 1.0
  cloudEvents_type: order.placed
  cloudEvents_source: /order-service
  cloudEvents_id: 019462a7-...
  content-type: application/json

Body: {"orderId":"...","customerEmail":"...","total":99.99}
```

Binary mode is the default and recommended for most use cases. It keeps the payload clean and allows intermediaries to route based on headers without parsing the body.

### Structured Mode

All CloudEvents attributes and the data payload are combined into a single JSON envelope in the message body.

```json
Body:
{
  "specversion": "1.0",
  "type": "order.placed",
  "source": "/order-service",
  "id": "019462a7-...",
  "datacontenttype": "application/json",
  "data": {"orderId":"...","customerEmail":"...","total":99.99}
}
```

Use structured mode when:

- Intermediaries need to inspect CloudEvents attributes but can only access the body
- You need full CloudEvents context in a single self-describing payload

Configure the content mode:

```csharp
bus.ConfigureCloudEvents(ce =>
{
    ce.ContentMode = CloudEventsContentMode.Structured;
});
```

## AMQP Mapping

When using the RabbitMQ transport, the `CloudEventsAmqpMapper` handles encoding and decoding. In binary mode, CloudEvents attributes are set as AMQP application properties with the `cloudEvents_` prefix. The mapper automatically:

- Maps outgoing `MessageProperties` to AMQP headers on publish
- Maps incoming AMQP headers back to `MessageProperties` on consume
- Propagates W3C trace context through `traceparent` and `tracestate` headers

## Trace Context Propagation

Ratatoskr injects W3C trace context into CloudEvents attributes automatically:

1. **On publish:** `Activity.Current.Id` and `Activity.Current.TraceStateString` are set as `traceparent` and `tracestate`
2. **On consume:** The trace context is extracted and used to create a child `Activity`, continuing the distributed trace

This enables end-to-end distributed tracing across services without any manual instrumentation. See [Observability](observability.md) for the complete tracing setup.

## What's Next

- [RabbitMQ](rabbitmq.md) — Transport-specific CloudEvents AMQP mapping details
- [Observability](observability.md) — OpenTelemetry tracing and metrics
- [Messages & Handlers](messages-handlers.md) — Message type definitions and serialization
