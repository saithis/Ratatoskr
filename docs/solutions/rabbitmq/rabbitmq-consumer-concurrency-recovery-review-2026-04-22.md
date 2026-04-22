---
title: "RabbitMQ consumer concurrency and recovery review findings"
category: rabbitmq
date: 2026-04-22
tags: [rabbitmq, dotnet-client, concurrency, recovery, consumer, publishing, tests]
module: Ratatoskr.RabbitMq
symptom: "Potential channel-level protocol errors and ambiguous recovery behavior under load/failure"
root_cause: "Shared channel operations are performed concurrently without explicit serialization and recovery strategy is not explicitly configured"
---

## Context

Review scope focused on:

- `src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs`
- `src/Ratatoskr.RabbitMq/RabbitMqMessageSender.cs`
- `src/Ratatoskr.RabbitMq/RabbitMqConnectionManager.cs`
- integration tests in `tests/Ratatoskr.Tests/Integration/RabbitMqConsumerShutdownTests.cs`

Cross-checked against RabbitMQ .NET API guide sections on concurrency and automatic recovery:

- https://www.rabbitmq.com/client-libraries/dotnet-api-guide#concurrency
- https://www.rabbitmq.com/client-libraries/dotnet-api-guide#recovery

## Findings

### 1) Shared publish channel used concurrently (critical)

`RabbitMqMessageSender` reuses one send channel (`GetOrCreateSendChannelAsync`) and publishes without a send lock. RabbitMQ guidance explicitly warns that concurrent operations on a shared `IChannel` must be serialized, especially publishing.

Risk:

- Frame interleaving / continuation exceptions
- connection-level protocol errors under parallel publish pressure

### 2) Ack/Nack operations can run concurrently on one consume channel (critical)

`RabbitMqConsumer` dispatches handler work in parallel (`DispatchAfterGateAsync`) and each task may ack/nack via the same channel concurrently. RabbitMQ guidance says multi-threaded consumers sharing a channel must mutually exclude acknowledgement operations.

Risk:

- double-ack / channel exceptions in concurrent completion scenarios
- nondeterministic failures under load

### 3) Recovery behavior is not explicitly configured (high)

`ConnectionFactory` is created with only `Uri` set. Recovery-relevant settings (`AutomaticRecoveryEnabled`, `TopologyRecoveryEnabled`, `NetworkRecoveryInterval`, connection naming) are not explicitly configured in code.

Risk:

- Behavior depends on library defaults instead of project intent
- possible conflict between library auto-recovery behavior and custom reconnect loop logic

### 4) Fire-and-forget handler dispatch obscures failure surfaces (high)

Consumer dispatch uses unawaited tasks (`_ = DispatchAfterGateAsync(...)`). This reduces determinism and can hide ack/nack failure outcomes, especially during channel shutdown and reconnect transitions.

Risk:

- hard-to-diagnose operational failures
- errors observed only as secondary symptoms

### 5) Prefetch edge case can allow unbounded memory pressure (medium)

`PrefetchCount = 0` (unlimited) is allowed. Deliveries are cloned before processing gate acquisition. Under burst traffic this can accumulate many buffered payload copies.

Risk:

- memory amplification during spikes
- reduced backpressure guarantees

### 6) Semantic channel errors can be treated as reconnectable noise (medium)

Reconnect loop retries broadly on exceptions. Channel-level semantic failures (topology mismatch, invalid declarations) can look like transient disconnect behavior.

Risk:

- endless reconnect loops that hide actionable misconfiguration
- noisy logs and delayed diagnosis

## Did we implement concurrency and recovery correctly?

Not fully.

- Concurrency: current publish and ack patterns do not clearly satisfy RabbitMQ guidance for safe shared-channel usage under parallel execution.
- Recovery: strategy is partially implemented (custom reconnect loop) but not explicitly defined end-to-end against client recovery options, making behavior ambiguous under real failures.

## Missing edge-case tests

### Concurrency tests

- Parallel publish stress test on shared sender channel (`PublishDirectAsync` from many tasks) to detect protocol/continuation failures.
- Parallel handler completion with `ConcurrencyLimit > 1` to validate ack/nack serialization safety and channel stability.

### Recovery tests

- Broker restart/network interruption integration test validating consumer recovery, resumed processing, and expected redelivery behavior.
- Disconnect between delivery and ack test to verify stale-tag/redelivery behavior remains safe after recovery.
- Recovery + retry-path test (failure during DLQ/retry publish handling).

### Configuration/guardrail tests

- Explicit behavior test for semantic topology errors: fail fast vs reconnect forever.
- `PrefetchCount = 0` burst test to verify memory/backpressure expectations.

## Recommended next hardening steps

- Serialize shared-channel publish operations or move to dedicated channel-per-publisher pattern.
- Serialize ack/nack/reject operations per consume channel when handlers run concurrently.
- Make recovery configuration explicit in `ConnectionFactory` and align it with custom reconnect ownership.
- Add integration tests above before changing behavior so regressions are measurable.
