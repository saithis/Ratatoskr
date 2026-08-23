# Feature Proposals & Roadmap Plan

This document outlines planned feature additions and enhancement proposals for **Ratatoskr**, ranked by usefulness and operational impact. 

> [!NOTE]
> Transports (e.g., Kafka, Azure Service Bus, AWS SQS) are intentionally excluded from this document as they are scheduled for a separate transport-focused roadmap phase.

---

## Ranked Feature Proposals

### 1. Embedded Web Management Dashboard (`Ratatoskr.UI`)
* **Impact**: **Critical (High Operational Value)**
* **Category**: Operations & UI

#### Description
An embedded ASP.NET Core admin dashboard (`app.MapRatatoskrUI("/ratatoskr")`) that eliminates the need to run manual SQL queries against `OutboxMessages` or `InboxHandlerStatuses` during incident response or operations.

#### Key Capabilities
* **Poison Message Workbench**: Search, inspect CloudEvents JSON payloads, view exception stack traces, and trigger single-click or batch retries.
* **Payload Editor**: Modify payload JSON prior to replaying (crucial for fixing malformed events caused by validation errors or broken third-party payloads).
* **Live System Metrics & Telemetry**: Visualize real-time Outbox/Inbox lag, processing duration histograms, and poison message counts.
* **Channel Topology Explorer**: Inspect active channels, registered handlers, message types, and transport bindings.

---

### 2. Message Scheduling & Deferred Delivery (Delayed Outbox)
* **Impact**: **High**
* **Category**: Core Engine

#### Description
Built-in delayed publishing capability (e.g., `bus.PublishAsync(event, options => options.DeliverAt(...))`) to handle business workflows requiring delayed execution without requiring external dependencies like Quartz.NET or Hangfire.

#### Technical Details
* Add an optional `ScheduledAt` timestamp column to `OutboxMessages`.
* Update `OutboxProcessor` polling queries to ignore messages where `ScheduledAt > DateTimeOffset.UtcNow`.

```csharp
await bus.PublishAsync(
    new OrderPaymentReminderEvent(orderId),
    options => options.DeliverAt(DateTimeOffset.UtcNow.AddHours(24)),
    cancellationToken);
```

---

### 3. Programmatic Management & Replay API (`Ratatoskr.Management`)
* **Impact**: **High**
* **Category**: Operations & API

#### Description
A programmatic C# API (`IRatatoskrManagementService`) and secure HTTP/REST endpoint suite for querying, retrying, archiving, and purging Outbox and Inbox durability states programmatically.

#### Key Capabilities
* `GetPoisonedMessagesAsync(PaginationOptions options)`
* `RetryPoisonedMessageAsync(Guid messageId)`
* `ArchivePoisonedMessagesAsync(DateTimeOffset failedBefore)`
* `PurgeCompletedMessagesAsync(TimeSpan olderThan)`
* Policy-based endpoint security (e.g., `.RequireAuthorization("RatatoskrAdmin")`).

---

### 4. Message Pipeline & Interceptor Middleware (`IMessageFilter`)
* **Impact**: **High**
* **Category**: Extensibility

#### Description
An extensible middleware pipeline (similar to ASP.NET Core middleware or MediatR pipeline behaviors) executed before message persistence or handler execution.

#### Key Capabilities
* **Payload Encryption / PII Masking**: Automatically encrypt sensitive CloudEvents data attributes before outbox insertion and decrypt on consume.
* **Validation**: Run `FluentValidation` filters before enqueuing to outbox or consuming in inbox.
* **Tenant Context Propagation**: Extract tenant headers from HTTP context and inject them into CloudEvents attributes automatically.

```csharp
public class PiiMaskingFilter : IMessageFilter
{
    public async Task InvokeAsync(MessageContext context, MessageDelegate next)
    {
        // Pre-processing (e.g., encrypt fields)
        await next(context);
        // Post-processing
    }
}
```

---

### 5. Schema Evolution & Versioning Framework (`IMessageMigrator`)
* **Impact**: **Medium-High**
* **Category**: Resilience

#### Description
Contract mapping and migration infrastructure to support zero-downtime rolling deployments where event schemas evolve across versions.

#### Key Capabilities
* Register message schema mappers/migrators to upgrade or downgrade event contracts on-the-fly during inbox deserialization:
```csharp
public class OrderPlacedV1ToV2Migrator : IMessageMigrator<OrderPlacedV1, OrderPlacedV2>
{
    public OrderPlacedV2 Migrate(OrderPlacedV1 oldEvent) =>
        new OrderPlacedV2(oldEvent.OrderId, oldEvent.Amount, Currency: "USD");
}
```
* JSON Schema validation against CloudEvents `dataschema` attributes to detect breaking changes early.

---

### 6. Batching & Bulk Processing (`IBatchMessageHandler<T>`)
* **Impact**: **Medium-High**
* **Category**: Performance

#### Description
Support for batch consumption of messages in the Inbox to reduce database transaction overhead and network roundtrips for high-throughput stream-like message flows.

#### Key Capabilities
* Support `IBatchMessageHandler<TMessage>` for processing `IReadOnlyList<TMessage>`.
* Configurable `BatchSize` and `BatchTimeout` for inbox handlers.
* Transactional acknowledgment for the entire batch.

---

### 7. Multi-Tenancy & Tenant-Scoped Durability
* **Impact**: **Medium**
* **Category**: Architecture

#### Description
First-class support for multi-tenant applications using database-per-tenant or schema-per-tenant patterns.

#### Key Capabilities
* Tenant-aware outbox staging and background processor dispatching.
* Automatic injection of tenant IDs into CloudEvents attributes (`tenantid` extension attribute) and W3C trace state.
* Dynamic `DbContext` resolver support for tenant-bound Outbox/Inbox tables.

---

### 8. Chaos Injection & Resilience Testing Suite (`Ratatoskr.Testing.Chaos`)
* **Impact**: **Medium**
* **Category**: Testing

#### Description
Utilities built into `Ratatoskr.Testing` to simulate real-world failure modes in integration test suites.

#### Key Capabilities
* Simulate database transaction rollbacks mid-outbox flush.
* Inject intermittent network drops or lock loss during Inbox processing.
* Verify poison message handling, threshold backoffs, and stuck message recoveries under fault conditions.

---

## Priority Summary Matrix

| Rank | Feature | Core Area | Impact Level |
| :--- | :--- | :--- | :--- |
| **1** | **`Ratatoskr.UI` Dashboard** | Operations / UI | Critical |
| **2** | **Message Scheduling & Deferred Delivery** | Core Engine | High |
| **3** | **Programmatic Management & Replay API** | Operations / API | High |
| **4** | **Message Pipeline & Interceptors** | Extensibility | High |
| **5** | **Schema Evolution & Versioning** | Resilience | Medium-High |
| **6** | **Batching & Bulk Processing** | Performance | Medium-High |
| **7** | **Multi-Tenancy Support** | Architecture | Medium |
| **8** | **Chaos & Fault Injection Suite** | Testing | Medium |
