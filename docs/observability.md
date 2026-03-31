# Observability

Ratatoskr integrates with [OpenTelemetry](https://opentelemetry.io/) for distributed tracing and metrics. All instrumentation uses the standard `System.Diagnostics` APIs — `ActivitySource` for tracing and `Meter` for metrics.

## Setup

Register the Ratatoskr activity source and meter with the .NET OpenTelemetry SDK:

[!code-csharp[](../examples/Docs/Program.cs#ConfigureOpenTelemetry)]

The constants `RatatoskrDiagnostics.ActivitySourceName` and `RatatoskrDiagnostics.MeterName` are both `"Ratatoskr"`.

## Tracing

Ratatoskr creates `Activity` spans at key pipeline stages:

- **Publish** — A span is created when `PublishDirectAsync` is called, covering serialization and transport send
- **Consume** — A span is created when a message is received from the transport, covering routing and dispatch
- **Dispatch** — A child span covers handler invocation
- **Outbox/Inbox** — Spans cover background processor batch operations

### W3C Trace Context Propagation

Trace context is automatically propagated through messages:

1. On publish, `Activity.Current.Id` and `TraceStateString` are injected into `MessageProperties` as `traceparent` and `tracestate`
2. On consume, the trace context is extracted and used to create a child activity, continuing the distributed trace across services

This means your existing APM tools (Jaeger, Zipkin, Azure Monitor, Datadog, etc.) will show end-to-end traces spanning publish → transport → consume → handle.

## Metrics Reference

All metrics are emitted on the `"Ratatoskr"` meter.

### Standard OpenTelemetry Messaging Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `messaging.client.operation.duration` | Histogram | `s` | Duration of messaging operation initiated by a producer or consumer client |
| `messaging.client.sent.messages` | Counter | `{message}` | Number of messages producer attempted to send to the broker |
| `messaging.client.consumed.messages` | Counter | `{message}` | Number of messages delivered to the application |
| `messaging.process.duration` | Histogram | `s` | Duration of processing operation |

### Lag Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `ratatoskr.receive.lag` | Histogram | `s` | Time from message creation to reception |
| `ratatoskr.process.lag` | Histogram | `s` | Time from message creation to processing completion |

### Reliability Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `ratatoskr.retry.messages` | Counter | `{message}` | Messages scheduled for retry |
| `ratatoskr.dead_letter.messages` | Counter | `{message}` | Messages sent to DLQ |

### Outbox Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `ratatoskr.outbox.process.count` | Counter | `{message}` | Messages processed from the outbox (tagged `status=success\|failure`) |
| `ratatoskr.outbox.poison.count` | Counter | `{message}` | Outbox messages marked as poisoned |
| `ratatoskr.outbox.process.duration` | Histogram | `s` | Duration of outbox processing batch |
| `ratatoskr.outbox.batch.size` | Histogram | `{message}` | Messages picked up per outbox batch |

### Inbox Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `ratatoskr.inbox.deliver.count` | Counter | `{message}` | Inbox handler deliveries attempted (tagged `status`) |
| `ratatoskr.inbox.poison.count` | Counter | `{message}` | Inbox handler statuses marked as poisoned |
| `ratatoskr.inbox.process.duration` | Histogram | `s` | Duration of inbox processing batch |
| `ratatoskr.inbox.batch.size` | Histogram | `{message}` | Handler statuses picked up per inbox batch |

### Cleanup Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `ratatoskr.outbox.cleanup.count` | Counter | `{message}` | Processed outbox messages deleted by cleanup |
| `ratatoskr.outbox.cleanup.duration` | Histogram | `s` | Duration of outbox cleanup operation |
| `ratatoskr.inbox.cleanup.status.count` | Counter | `{status}` | Completed inbox handler statuses deleted by cleanup |
| `ratatoskr.inbox.cleanup.message.count` | Counter | `{message}` | Orphaned inbox messages deleted by cleanup |
| `ratatoskr.inbox.cleanup.duration` | Histogram | `s` | Duration of inbox cleanup operation |

### Distributed Lock Metrics

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `ratatoskr.lock.acquisition.failure` | Counter | `{attempt}` | Failed lock acquisitions |
| `ratatoskr.lock.lost` | Counter | `{event}` | Lock losses during processing |

## Message Activity Observers

`IMessageActivityObserver` implementations are notified at pipeline stages for custom instrumentation:

| Stage | When |
|-------|------|
| `Published` | After each send attempt during `PublishDirectAsync` |
| `Sent` | After bytes are sent to the transport |
| `Received` | When the consumer receives a message from the transport |
| `Dispatched` | After handler invocation completes |
| `OutboxStaged` | When a message is serialized into an outbox entity during `SaveChanges` |
| `OutboxSent` | When the outbox processor sends a message to the transport |
| `OutboxPoisoned` | When an outbox message exceeds max retries |
| `InboxQueued` | When a message is accepted into the inbox |
| `InboxDispatched` | When an inbox handler delivery is attempted (success or failure) |
| `InboxPoisoned` | When an inbox handler status exceeds max retries |

> [!NOTE]
> Observers are designed for **instrumentation and testing** — not for reliable side effects. Observer exceptions are caught and logged at `Warning` level. They never affect the message pipeline.

The `Ratatoskr.Testing` package uses `IMessageActivityObserver` internally to power `MessageTrackingSession`. See [Testing](testing.md) for details.

## Example Prometheus Queries

Monitor key health indicators with these PromQL queries:

```promql
# Outbox poison rate (should be 0)
rate(ratatoskr_outbox_poison_count_total[5m])

# Inbox poison rate (should be 0)
rate(ratatoskr_inbox_poison_count_total[5m])

# Outbox processing throughput
rate(ratatoskr_outbox_process_count_total{status="success"}[5m])

# Average receive lag
rate(ratatoskr_receive_lag_sum[5m]) / rate(ratatoskr_receive_lag_count[5m])

# Lock acquisition failures (infrastructure issues)
rate(ratatoskr_lock_acquisition_failure_total[5m])

# P99 message processing duration
histogram_quantile(0.99, rate(messaging_process_duration_bucket[5m]))
```

## What's Next

- [Operations](operations.md) — Alert thresholds and monitoring runbook
- [Testing](testing.md) — Using message tracking sessions for test assertions
- [Architecture](architecture.md) — Where tracing spans are created in the pipeline
