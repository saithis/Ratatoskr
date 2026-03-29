# Enterprise Readiness Acceptance Checklist

This checklist is intended for vendor sign-off and payment decisions for `CloudEventBus` (Ratatoskr-based stack).  
Use this as a hard gate: every mandatory item must pass with evidence.

## Decision Gates

- `Gate A (Must-pass before payment)`: all Critical and High items resolved and verified.
- `Gate B (Can be post-payment if contractually agreed)`: Medium and Low items with accepted mitigation and target date.
- Final decision values:
  - `Accept`
  - `Accept with conditions`
  - `Reject`

## Current Recommendation (Based on Audit)

- Status: `Reject as-is` / `Pay only after fixes`
- Reason: unresolved issues include 1 Critical and 2 High risks.

## Must-Fix Before Payment (Gate A)

### A1. Stable message ID for deduplication (Critical)

- Risk: In RabbitMQ binary consume mode, if incoming ID is missing, code generates a new GUID, which can break inbox dedup semantics.
- Evidence path: `src/Ratatoskr.RabbitMq/CloudEventsAmqpMapper.cs`
- Required acceptance criteria:
  - Incoming messages used with inbox dedup must require a stable external ID.
  - No random ID fallback in dedup-critical path, or explicit reject/quarantine behavior exists.
  - Integration test proves duplicate delivery of same wire message maps to same dedup key.
- Verification test to add/run:
  - Redelivery scenario where producer omits ID must not create multiple logical message IDs for same delivery chain.

### A2. Unroutable publish detection (High)

- Risk: publish uses `mandatory: false`; unroutable messages may be silently dropped depending on topology and broker behavior.
- Evidence path: `src/Ratatoskr.RabbitMq/RabbitMqMessageSender.cs`
- Required acceptance criteria:
  - Unroutable publishes are detectable and surfaced as failure (or reliably audited).
  - Operational metric/log clearly identifies unroutable condition.
  - Integration test verifies unroutable message is not silently considered successful.

### A3. Security trust boundary and message authenticity posture (High)

- Risk: wire metadata (`type`, headers) controls dispatch/deserialization; no built-in message authenticity/signature.
- Evidence paths:
  - `src/Ratatoskr/Core/MessageDispatcher.cs`
  - `src/Ratatoskr.RabbitMq/CloudEventsAmqpMapper.cs`
- Required acceptance criteria:
  - Security section in docs defines required broker/network controls (ACLs, TLS, trusted publishers).
  - Threat model explicitly states assumptions and failure modes.
  - Optional but preferred: pluggable signature/integrity validation hook before dispatch.

## Requirement-by-Requirement Acceptance Matrix

For each line, mark `Pass` or `Fail` and attach evidence (test report, logs, code links).

### 1) At-least-once message guarantee

- Required:
  - Outbox retries until success or poison state.
  - Inbox retries per handler with dedup semantics.
  - Crash/restart behavior documented and tested.
- Evidence:
  - `docs/architecture.md`
  - `src/Ratatoskr.EfCore/Internal/OutboxMessageProcessor.cs`
  - `src/Ratatoskr.EfCore/Internal/InboxAcceptor.cs`
  - `tests/Ratatoskr.Tests/Integration/Outbox/OutboxDurabilityTests.cs`
  - `tests/Ratatoskr.Tests/Integration/Inbox/InboxDeduplicationTests.cs`

### 2) AsyncAPI topology docs with EventCatalog extras

- Required:
  - Generated AsyncAPI includes channels/operations/messages.
  - EventCatalog extensions (`x-eventcatalog-*`) present.
- Evidence:
  - `src/Ratatoskr/AsyncApi/Generation/AsyncApiDocumentGenerator.cs`
  - `tests/Ratatoskr.Tests/AsyncApi/AsyncApiDocumentGeneratorTests.cs`
  - `docs/asyncapi.md`

### 3) Compatibility with non-project services and heterogeneous schemas/headers

- Required:
  - Extensibility via custom serializer and envelope mapper.
  - Clear documented contract for external producers (CloudEvents type, content type, headers).
  - At least one integration example with non-default mapping.
- Evidence:
  - `src/Ratatoskr/Core/IMessageSerializer.cs`
  - `src/Ratatoskr.RabbitMq/IRabbitMqEnvelopeMapper.cs`
  - `docs/messages-handlers.md`

### 4) Default CloudEvents + RabbitMQ standard headers

- Required:
  - Default mapping outputs CloudEvents AMQP headers.
  - Incoming mapper supports expected header forms.
- Evidence:
  - `src/Ratatoskr.RabbitMq/CloudEventsAmqpMapper.cs`
  - `tests/Ratatoskr.Tests/RabbitMq/CloudEventsAmqpMapperIncomingTests.cs`
  - `tests/Ratatoskr.Tests/RabbitMq/CloudEventsAmqpMapperOutgoingTests.cs`

### 5) Transactional integrity with EF Core saves

- Required:
  - Business data + outbox staging in same transaction.
  - Same-DbContext optimization behavior documented.
- Evidence:
  - `src/Ratatoskr.EfCore/Internal/OutboxTriggerInterceptor.cs`
  - `docs/outbox.md`
  - `tests/Ratatoskr.Tests/Integration/Outbox/OutboxProcessingTests.cs`

### 6) Automatic retry + manual retry after configured limit

- Required:
  - Retry budget configurable for outbox/inbox/transport.
  - Poison state after max retries.
  - Manual retry procedure documented and tested operationally.
- Evidence:
  - `src/Ratatoskr.RabbitMq/RabbitMqRetryHandler.cs`
  - `docs/operations.md`
  - `tests/Ratatoskr.Tests/Integration/RetryTests.cs`

### 7) Multiple DbContext compatibility

- Required:
  - Separate processors/locks/config per DbContext.
  - No cross-context interference.
- Evidence:
  - `tests/Ratatoskr.Tests/Integration/MultiDbContextTests.cs`
  - `docs/architecture.md`

### 8) Local inter-module messaging inside service (different DbContexts)

- Required:
  - Local send and inbox acceptance across DbContexts works reliably.
  - End-to-end integration test exists.
- Evidence:
  - `tests/Ratatoskr.Tests/Integration/MultiDbContextTests.cs`

### 9) Observability (tracing, metrics, logs)

- Required:
  - OTEL traces and metrics emitted for publish/send/consume/retry/dlq/outbox/inbox.
  - Setup docs are complete and accurate.
- Evidence:
  - `src/Ratatoskr/Core/RatatoskrDiagnostics.cs`
  - `docs/observability.md`
  - `tests/Ratatoskr.Tests/Integration/OpenTelemetry/OpenTelemetryTracingTests.cs`
  - `tests/Ratatoskr.Tests/Integration/OpenTelemetry/OpenTelemetryMetricsTests.cs`

### 10) Stable and bug free

- Required:
  - Critical and High risk register is empty.
  - Regression test suite passes.
- Evidence:
  - CI run results + targeted integration runs listed below.

### 11) Easy to maintain

- Required:
  - Architecture and module boundaries documented.
  - Validation catches misconfiguration early.
- Evidence:
  - `docs/architecture.md`
  - `tests/Ratatoskr.Tests/Integration/Inbox/InboxConfigurationTests.cs`
  - `tests/Ratatoskr.Tests/Core/InboxConfigurationValidatorTests.cs`

### 12) No security issues

- Required:
  - No unresolved High/Critical security findings.
  - Documented deployment requirements: broker ACLs, TLS, trusted publisher boundary.
  - Security test plan exists (including abuse cases).
- Evidence:
  - Security review report + implementation evidence.

### 13) Easy to use / intuitive behavior

- Required:
  - Docs match API signatures and behavior.
  - Onboarding sample works end to end with minimal surprises.
- Evidence:
  - `docs/getting-started.md`
  - `docs/messages-handlers.md`
  - `examples/Docs/Program.cs`

## Non-Functional Enterprise Risks (Often Missed)

These are additional gates outside the original requirements that frequently cause late production cost.

### N1) Ordering and causality semantics

- Risk:
  - At-least-once and retries can reorder processing.
  - Cross-channel and cross-DbContext flows can complete out of publish order.
- Required:
  - Explicit statement of where ordering is guaranteed and where it is not.
  - Business processes that require strict order have compensating design (partitioning, sequence checks, or saga logic).
  - Integration test for out-of-order replay does not violate business invariants.

### N2) Idempotency ownership per handler

- Risk:
  - Framework-level dedup does not replace business-level idempotency for side effects.
- Required:
  - Handler inventory with idempotency strategy per side effect (DB update, email, payment, external API call).
  - Idempotency key source and storage policy documented.
  - Duplicate-delivery tests for critical handlers.

### N3) Schema governance lifecycle

- Risk:
  - Multi-team evolution without governance causes breaking consumers and emergency hotfixes.
- Required:
  - Versioning policy (additive vs breaking) with approval workflow.
  - Backward compatibility test gate in CI.
  - Clear deprecation timeline and owner for each contract.

### N4) Operational recovery ownership and SLA

- Risk:
  - Manual retry/runbook actions can fail during incidents without clear ownership.
- Required:
  - On-call ownership defined for poison handling and retries.
  - Recovery SLA targets agreed (time-to-detect, time-to-recover).
  - Dry-run incident exercise completed using production-like data.

### N5) Capacity and failure-mode performance

- Risk:
  - Backlog growth, retry storms, lock contention, or DB bloat under failure can cause cascading outages.
- Required:
  - Load test baseline and failure-mode test (consumer down, downstream slow, broker reconnect churn).
  - Capacity limits and auto-scaling trigger thresholds documented.
  - Retention and cleanup verified at expected data volume.

### N6) Data governance and compliance

- Risk:
  - Outbox/inbox persistence can retain PII or sensitive business data longer than allowed.
- Required:
  - Data classification of message payloads and headers.
  - Retention and deletion policy aligned with legal requirements.
  - Encryption at rest/in transit and access audit controls verified.

### N7) Upgrade and rollback safety

- Risk:
  - Package or schema upgrades can break message processing in rolling deployments.
- Required:
  - Version upgrade playbook with migration and rollback steps.
  - Forward/backward compatibility test across old/new service versions.
  - Release checklist includes schema drift and operational verification.

### N8) Vendor support and contractual protection

- Risk:
  - Lack of enforceable support terms can make defects expensive post-payment.
- Required:
  - Severity-based SLA in contract.
  - Warranty window and defect remediation commitments.
  - Acceptance tied to objective test evidence, not only documentation claims.

## Verification Test Run Set (Minimum)

Run and attach output for each:

- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/OutboxDurabilityTests/*" --maximum-parallel-tests 10`
- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/RetryTests/*" --maximum-parallel-tests 10`
- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/MultiDbContextTests/*" --maximum-parallel-tests 10`
- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/ConsumeTests/*" --maximum-parallel-tests 10`
- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/AsyncApiDocumentGeneratorTests/*" --maximum-parallel-tests 10`
- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/OpenTelemetryTracingTests/*" --maximum-parallel-tests 10`
- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/OpenTelemetryMetricsTests/*" --maximum-parallel-tests 10`
- `dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/InboxDeduplicationTests/*" --maximum-parallel-tests 10`

## Contractual Acceptance Clauses (Suggested)

- Payment release requires:
  - all Gate A items closed and re-tested;
  - signed evidence bundle (test outputs + code refs + changelog);
  - rollback and operational runbook validated by your team.
- Include warranty period and defect severity SLAs:
  - Critical: fix/workaround within 24-48h
  - High: within 5 business days
  - Medium: within 2 sprints

## Final Sign-Off Block

- Gate A status: `PASS / FAIL`
- Gate B status: `PASS / CONDITIONAL`
- Residual risks accepted by: `Name, Role, Date`
- Final decision: `Accept / Accept with conditions / Reject`

