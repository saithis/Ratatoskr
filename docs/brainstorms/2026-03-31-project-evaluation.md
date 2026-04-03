# Combined Validated Evaluation (All Agent Reports)

**Date:** 2026-03-31
**Status:** Draft

Validation approach:
- Every finding was reviewed against the current implementation and docs.
- Code-level confirmation is noted where evidence was inspected.
- Duplicates were merged; the source(s) that raised each finding are cited.
- Each item carries a verdict: `Valid`, `Partially valid`, or `Invalid`.
- Items are organized into: active findings (by priority), performance findings, accepted risks, user responsibility, not applicable, outdated findings, and deferred / future improvements.

---

## Section A — Functional Requirement Findings (Prioritized, High → Low)

### A-CRIT-2 · Unroutable RabbitMQ publish silently lost (`mandatory: false`)

- Verdict: `Valid`
- Severity: Critical / must-fix
- Detail: Both `RabbitMqMessageSender` and `RabbitMqRetryHandler` publish with `mandatory: false`. RabbitMQ will silently discard a message if no queue is bound. There is no return-handler, metric, or log for this condition.
- Evidence: `src/Ratatoskr.RabbitMq/RabbitMqMessageSender.cs` (line 50), `src/Ratatoskr.RabbitMq/RabbitMqRetryHandler.cs` (line 89)
- Source(s): Checklist (A2)

### A-CRIT-3 · Inbox deduplication undermined when inbound message lacks a stable ID

- Verdict: `Valid`
- Severity: Critical / must-fix
- Detail: The binary-mode incoming mapper falls back to `Guid.NewGuid()` when neither `BasicProperties.MessageId` nor the CloudEvents `id` header is present. This means the same wire delivery can be accepted as distinct inbox entries on redelivery, defeating the deduplication guarantee.
- Evidence: `src/Ratatoskr.RabbitMq/CloudEventsAmqpMapper.cs` (line 200–202)
- Source(s): Checklist (A1), Claude


### A-HIGH-3 · Wire metadata controls dispatch with no authenticity verification

- Verdict: `Valid`
- Severity: High
- Detail: Incoming AMQP headers (`type`, `source`, `traceparent`, etc.) are trusted unconditionally. An attacker with access to the broker can spoof dispatch-critical metadata. No built-in signature or integrity validator extension point exists.
- Evidence: `src/Ratatoskr.RabbitMq/CloudEventsAmqpMapper.cs`, `src/Ratatoskr/Core/MessageDispatcher.cs`
- Source(s): Checklist (A3), Claude

### A-HIGH-5 · Manual retry is SQL-only (no built-in API or UI)

- Verdict: `Partially valid`
- Detail: A SQL runbook for resetting poisoned outbox/inbox rows is documented in `docs/operations.md`. The finding is true that no programmatic API, CLI, or admin UI exists — operations teams must run raw SQL against production. The claim that "no manual retry mechanism exists" is incorrect; the mechanism exists but is operationally primitive.
- Evidence: `docs/operations.md` (§ Manual Retry)
- Source(s): Claude (original claim overstated), Gemini (overstated in the opposite direction)

### A-HIGH-7 · Graceful shutdown does not drain in-flight consumer messages

- Verdict: `Valid`
- Severity: Medium-High
- Detail: `RabbitMqConsumer.StopAsync` calls `base.StopAsync` and then immediately closes channels. It does not stop accepting new deliveries first and wait for in-flight handlers to finish ACKing. Unacknowledged messages are returned to the queue by RabbitMQ (safe but causes re-processing). Inbox messages whose `ProcessingStartedAt` was set will not be retried until the stuck-message threshold (default 5 min) expires.
- Evidence: `src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs` (lines 297–304)
- Source(s): Claude

### A-MED-1 · No per-channel serializer or content-type negotiation *(priority elevated from Medium)*

- Verdict: `Valid`
- Severity: High (interop)
- Detail: `IMessageSerializer` is registered as a single global singleton. No per-channel or per-message-type serializer configuration exists. Services using Protobuf, Avro, or custom binary formats on any channel require consumers to implement a multiplexer themselves.
- Evidence: `src/Ratatoskr/ServiceCollectionExtensions.cs`, `src/Ratatoskr/Serializers/Json/JsonMessageSerializer.cs`
- Source(s): Claude, Gemini

### A-MED-2 · No EF Core migration drift detection at startup

- Verdict: `Valid`
- Severity: Medium
- Detail: Entity configuration is defined programmatically; no migrations are shipped. When a library upgrade adds new columns, consumers must manually generate EF migrations. Entity classes are `internal`, so consumers cannot inspect what changed. The application simply crashes at runtime on schema mismatch — there is no startup health check for schema drift.
- Evidence: `src/Ratatoskr.EfCore/Internal/` (entity classes), `docs/operations.md` (§ EF Core Migrations)
- Source(s): Claude

### A-MED-3 · No message schema version negotiation or upgrader mechanism

- Verdict: `Valid`
- Severity: Medium
- Detail: No `IMessageUpgrader`, schema registry, or version component in `MessageProperties.Type`. The inbox stores raw `byte[]`; if the CLR type's shape changes, deserialization of old messages fails or silently loses data. Only additive, backward-compatible changes are safe. Breaking changes require fully draining in-flight inbox/outbox rows first.
- Evidence: `src/Ratatoskr/Core/MessageProperties.cs`, `src/Ratatoskr.EfCore/Internal/InboxMessageEntity.cs`
- Source(s): Claude

### A-MED-7 · Inbox is opt-in per channel; channels without `UseInbox()` have no deduplication

- Verdict: `Valid`
- Severity: Medium (developer error risk)
- Detail: A developer who subscribes a new channel but omits `UseInbox()` has no framework-level protection against duplicate delivery. In "at-least-once" systems, duplicate delivery is guaranteed eventually. Team guidelines must mandate idempotent handlers or explicit `UseInbox()` adoption.
- Evidence: `src/Ratatoskr.EfCore/` registration paths, `docs/inbox.md`
- Source(s): Gemini

### A-LOW-1 · No per-channel CloudEvents content mode *(priority elevated from Low)*

- Verdict: `Valid`
- Severity: Medium
- Detail: Content mode (Binary vs Structured) is configured globally; you cannot use binary for high-throughput internal channels and structured for external partner channels simultaneously.
- Evidence: `src/Ratatoskr/CloudEvents/CloudEventsOptions.cs`
- Source(s): Claude

### A-LOW-10 · Inbox/outbox code duplication (shared ~80% of logic)

- Verdict: `Valid`
- Severity: Low
- Detail: `InboxOptions`/`OutboxOptions`, `InboxBuilder`/`OutboxBuilder`, and `InboxCleanupService`/`OutboxCleanupService` share ~80% of their structure with no base class.
- Evidence: `src/Ratatoskr.EfCore/`
- Source(s): Claude

### A-MED-10 · Configuration fully frozen at startup (no hot-reload) *(priority reduced from Medium)*

- Verdict: `Valid`
- Severity: Low
- Detail: No `IOptionsMonitor` or `IOptionsSnapshot` is used anywhere. Changing batch sizes, retry counts, polling intervals, or queue bindings requires a full application restart.
- Evidence: `src/Ratatoskr.EfCore/InboxOptions.cs`, `src/Ratatoskr.EfCore/OutboxOptions.cs`, `src/Ratatoskr.RabbitMq/RabbitMqOptions.cs`
- Source(s): Claude

### A-MED-11 · Transport abstraction is tightly coupled to AMQP semantics *(priority reduced from Medium)*

- Verdict: `Valid`
- Severity: Low
- Detail: The `Ratatoskr.RabbitMq` transport assumes AMQP routing models (exchanges, routing keys, AMQP headers). Migrating to Azure Service Bus, Kafka, or another broker would require a near-ground-up rebuild of the transport module, not just a new implementation of a thin interface.
- Evidence: `src/Ratatoskr.RabbitMq/CloudEventsAmqpMapper.cs`, `src/Ratatoskr.RabbitMq/RabbitMqTopologyManager.cs`
- Source(s): Gemini

---

## Section A-PERF — Performance Findings

Performance-only concerns that do not affect correctness or functional requirements. Address when throughput becomes a constraint. Items use the `A-PERF-*` prefix (not `A-MED-*` / `A-LOW-*`).

### A-PERF-1 · Single RabbitMQ send channel serializes concurrent publishes

- Verdict: `Valid`
- Severity: Medium (performance ceiling)
- Detail: `RabbitMqConnectionManager.GetOrCreateSendChannelAsync` returns a single shared AMQP channel protected by a semaphore. With publisher confirms, all outgoing messages are effectively serialized.
- Evidence: `src/Ratatoskr.RabbitMq/RabbitMqConnectionManager.cs` (lines 33–71)
- Source(s): Claude

### A-PERF-2 · Per-message `SaveChangesAsync` in outbox and inbox processors (throughput bottleneck)

- Verdict: `Valid`
- Severity: Medium (performance)
- Detail: Each message/status update within a batch triggers a separate `SaveChangesAsync` (lines 150 and 201 respectively). With `BatchSize = 100`, this is 100 DB round-trips per batch.
- Evidence: `src/Ratatoskr.EfCore/Internal/OutboxMessageProcessor.cs` (line 150), `src/Ratatoskr.EfCore/Internal/InboxMessageProcessor.cs` (line 201)
- Source(s): Claude

### A-PERF-3 · Consumer processes messages sequentially (no concurrency option)

- Verdict: `Partially valid`
- Severity: Medium (performance)
- Detail: No explicit `ConcurrencyLimit` option exists on `RabbitMqChannelOptions`. The actual per-message throughput ceiling also depends on broker-side prefetch and RabbitMQ .NET client v7 async consumer behavior. Adding an explicit concurrency control would be a clear improvement.
- Evidence: `src/Ratatoskr.RabbitMq/RabbitMqConsumer.cs`, `src/Ratatoskr.RabbitMq/Config/RabbitMqChannelOptions.cs`
- Source(s): Claude

### A-PERF-4 · EF Core Outbox is not viable for hyper-scale (>5k msgs/sec)

- Verdict: `Valid`
- Severity: Medium (scale ceiling, known limitation)
- Detail: The optimistic concurrency loop under high write contention causes CPU pressure on the DB. WAL-tailing approaches (Debezium, etc.) would be needed for true high-throughput scenarios. This is an architectural constraint of any EF Core outbox, not unique to this library.
- Evidence: `src/Ratatoskr.EfCore/Internal/OutboxMessageProcessor.cs`
- Source(s): Gemini

---

## Section B — Security Findings

### B-SEC-3 · No rate limiting on inbox/outbox batch processing (backlog burst risk)

- Verdict: `Valid`
- Severity: Low
- Detail: Both processors loop continuously through batches with no inter-batch delay. A large backlog can consume all available database connections.
- Evidence: `src/Ratatoskr.EfCore/Internal/OutboxProcessor.cs`, `src/Ratatoskr.EfCore/Internal/InboxProcessor.cs`
- Source(s): Claude

### B-SEC-5 · `HandlerInvokerCache` is a potentially unbounded dictionary

- Verdict: `Valid`
- Severity: Low
- Detail: `HandlerInvokerCache` uses `ConcurrentDictionary<Type, ...>`. Currently safe because message types are fixed at startup, but this assumption is not enforced — if dynamic type resolution were added, the dictionary would grow unboundedly.
- Evidence: `src/Ratatoskr/Core/HandlerInvokerCache.cs`
- Source(s): Claude

### B-SEC-6 · No payload schema validation (type-whitelist exists, but payload content is unchecked)

- Verdict: `Valid`
- Severity: Low-Medium
- Detail: `ChannelRegistry` acts as a type whitelist at startup (good), but the deserialized payload contents are never validated against a schema. Malformed payloads that satisfy the CLR type shape but violate business constraints pass silently.
- Evidence: `src/Ratatoskr/Serializers/Json/JsonMessageSerializer.cs`, `src/Ratatoskr/Core/MessageDispatcher.cs`
- Source(s): Claude

---

## Section C — Test Coverage Gaps

All gaps below were identified by Claude. None have been re-tested as part of this validation pass; they are listed as reported.

| # | Severity | Missing test scenario |
|---|----------|-----------------------|
| T1 | Medium | Outbox `MaxMessageSize` validation and transaction rollback |
| T2 | Medium | Outbox concurrent processor contention (`DbUpdateConcurrencyException` path) |
| T3 | Low | `PollingBackgroundService` distributed lock loss handling |
| T4 | Low | Inbox message `ReceivedAt` timestamp correctness |
| T5 | Low | `OutboxStagingCollection.Add(object)` non-generic overload |
| T6 | Low | `InboxMessageProcessor` missing message record (deleted between query and lookup) |
| T7 | Low | Inbox `SerializedProperties` deserialization failure poisoning |
| T8 | Low | Concurrent deduplication test may not reliably exercise true concurrency |

---

## Section D — Non-Functional Enterprise Risks

These are procurement/adoption risks beyond the functional requirement set. They do not necessarily require code changes but must have documented positions before enterprise sign-off.

### D-NF-1 · Ordering and causality semantics are not formally documented

- Verdict: `Valid`
- Severity: Medium
- Detail: At-least-once delivery with multi-instance processing does not preserve strict message ordering. This is not documented as an explicit constraint. Business processes requiring causal ordering need compensating design (partitioning, sequence checks, sagas).
- Source(s): Checklist (N1), Gemini

### D-NF-2 · Idempotency ownership per handler is not addressed at framework level

- Verdict: `Valid`
- Severity: Medium
- Detail: Framework dedup only covers (MessageId, HandlerKey) delivery deduplication. Business-level idempotency for side effects (DB mutations, external API calls, email sends, payment operations) is entirely the consumer's responsibility with no guidance or extension point.
- Source(s): Checklist (N2)

### D-NF-3 · No schema governance lifecycle or versioning policy

- Verdict: `Valid`
- Severity: Medium
- Detail: No versioning policy (additive vs breaking), approval workflow, backward-compatibility test gate, or deprecation timeline mechanism exists in the framework or its documentation.
- Source(s): Checklist (N3), Claude

### D-NF-5 · Capacity and failure-mode performance is untested

- Verdict: `Valid`
- Severity: Medium
- Detail: No load test baseline exists. No failure-mode test (consumer down, downstream slow, broker reconnect churn). No documented capacity limits or auto-scaling trigger thresholds. Retention and cleanup have not been verified at expected data volume.
- Source(s): Checklist (N5)

### D-NF-6 · Data governance and compliance posture is unaddressed

- Verdict: `Valid`
- Severity: Medium
- Detail: Outbox/inbox tables may retain PII or sensitive business data beyond legally permitted periods. No data classification, encryption-at-rest guidance, or access audit controls are documented or enforced by the framework.
- Source(s): Checklist (N6), Claude (exception messages finding)

### D-NF-7 · Upgrade and rollback safety is not documented or tested

- Verdict: `Valid`
- Severity: Medium
- Detail: No version upgrade playbook, forward/backward compatibility test across service versions, or release checklist for schema drift and operational verification exists. Related to the EF migration drift detection gap (A-MED-2) and rolling deployment risk (A-HIGH-6).
- Source(s): Checklist (N7), Claude

---

## Accepted Risks

These findings are valid but have been explicitly acknowledged and accepted as-is.

### A-LOW-11 · DLQ publish + ACK is non-atomic (can create DLQ duplicates on crash)

- Verdict: `Valid`
- Severity: Low
- Note: **Accepted risk.** At-least-once delivery semantics mean DLQ consumers must already be idempotent. This is consistent with the overall delivery model of the library.
- Detail: In `RabbitMqRetryHandler.RejectToDlqAsync`, the code first publishes to the DLQ exchange, then ACKs the original delivery. A process crash between these two operations causes the message to be redelivered by RabbitMQ and published to the DLQ again.
- Evidence: `src/Ratatoskr.RabbitMq/RabbitMqRetryHandler.cs` (lines 86–95)
- Source(s): Claude

### B-SEC-1 · Exception messages with potentially sensitive content persisted to DB

- Verdict: `Valid`
- Severity: Low
- Note: **Accepted.** The `Error` column is capped at 2000 chars. Library consumers are responsible for ensuring their handlers do not leak secrets in exception messages.
- Detail: Handler exception messages are stored in `OutboxMessageEntity.Error` and `InboxHandlerStatusEntity.LastError`. If exceptions leak connection strings, user data, or secrets, this data is permanently persisted.
- Evidence: `src/Ratatoskr.EfCore/Internal/OutboxMessageEntity.cs` (line 24, 93), `src/Ratatoskr.EfCore/Internal/InboxHandlerStatusEntity.cs`
- Source(s): Claude

### B-SEC-2 · RabbitMQ connection string (with credentials) held in-process as `Uri` singleton

- Verdict: `Valid`
- Severity: Low-Medium
- Note: **Accepted.** Standard practice for in-process connection string handling. Consumers who require enhanced secrets management (e.g. Azure Key Vault, Vault Agent) can inject credentials at configuration time via standard .NET configuration providers.
- Detail: `RabbitMqOptions.ConnectionString` of type `Uri?` typically contains plaintext AMQP credentials. It lives as a singleton for the process lifetime. There is no documentation recommending secrets-manager patterns or credential rotation.
- Evidence: `src/Ratatoskr.RabbitMq/RabbitMqOptions.cs`
- Source(s): Claude

### A-MED-12 · AsyncAPI output missing retry/DLQ topology and security scheme

- Verdict: `Partially valid`
- Severity: Low
- Note: **Accepted.** Retry queues and DLQs are internal implementation details of the library; consumers do not need to interact with them directly. Security scheme documentation is outside the scope of the AsyncAPI output.
- Detail: The generator correctly emits channels, operations, messages, AMQP bindings, and EventCatalog extensions. It does not document the retry queue, DLQ queue, or server-level security (TLS, vhost ACLs).
- Evidence: `src/Ratatoskr/AsyncApi/Generation/AsyncApiDocumentGenerator.cs`, `src/Ratatoskr.RabbitMq/AsyncApi/RabbitMqAsyncApiBindingProvider.cs`
- Source(s): Claude

---

## Deferred / Future Improvements

These findings are valid concerns but are not required for the initial users of this library. They may be revisited in future iterations.

### A-HIGH-9 · Multi-tenancy: zero framework support

- Verdict: `Valid`
- Severity: High (if multi-tenancy is required)
- Note: **Deferred.** Multi-tenancy support is not required for initial library adopters. Teams building multi-tenant SaaS using this library must provide their own isolation strategy (separate databases, separate queues, separate application instances). This is a candidate for a future major version.
- Detail: No tenant ID in `MessageProperties` or entity models. No tenant-scoped filtering in queries. Distributed locks are global per DbContext type, not per tenant. Each tenant requires entirely separate infrastructure.
- Evidence: `src/Ratatoskr/Core/MessageProperties.cs`, `src/Ratatoskr.EfCore/Internal/OutboxMessageProcessor.cs`
- Source(s): Claude

### A-LOW-7 · No YAML output for AsyncAPI endpoint

- Verdict: `Valid`
- Severity: Low
- Detail: The endpoint serializes JSON only; many AsyncAPI toolchains prefer YAML.
- Evidence: `src/Ratatoskr/AsyncApi/Extensions/AsyncApiEndpointExtensions.cs`
- Source(s): Claude

### A-MED-8 · Strict message ordering is not preserved under horizontal scale-out

- Verdict: `Valid`
- Severity: Medium (semantic / correctness risk)
- Detail: Outbox/inbox processors poll the DB in batches (`Take(BatchSize)`) and process asynchronously. Multiple worker instances grab overlapping batches in parallel, destroying chronological delivery ordering. Business processes that assume `OrderUpdated` always follows `OrderCreated` must implement compensating logic (sequence numbers, sagas, etc.).
- Evidence: `src/Ratatoskr.EfCore/Internal/OutboxMessageProcessor.cs`, `src/Ratatoskr.EfCore/Internal/InboxMessageProcessor.cs`
- Source(s): Gemini

---

## Summary

Overall architecture: sound. Core at-least-once guarantee: holds (with the staging bug caveat). Code quality: high (0 critical code quality issues).

