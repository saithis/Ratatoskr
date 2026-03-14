# Brainstorm: Ratatoskr Documentation Restructure

**Date:** 2026-03-12
**Status:** Draft

## What We're Building

A complete, restructured documentation site for Ratatoskr using DocFX. The docs will be written from scratch with a consistent voice and progressive structure, targeting both internal developers and open-source community members discovering the library for the first time.

**Style:** MassTransit/Wolverine-inspired — concept-focused guides with rich code examples, progressive disclosure from basics to advanced, but each page self-contained enough to read independently (hybrid approach).

**Code examples:** A new minimal example project (`examples/Docs/`) without Aspire dependencies, referenced from documentation pages. Uses an **order processing** domain throughout (OrderPlaced events, ProcessPayment commands, shipping notifications) for a consistent narrative across all pages. The existing `examples/PlaygroundApi` stays but is not the primary documentation reference.

**Database examples:** Provider-agnostic (EF Core level). Provider-specific details only in operations/advanced sections where unavoidable.

## Documentation Structure

### Navigation (toc.yml)

```yaml
- Introduction
- Getting Started
- Core Concepts
  - Architecture
  - Messages & Handlers
  - Channels & Routing
  - CloudEvents
- Transports
  - RabbitMQ
  - EF Core (Database)
- Durability
  - Outbox
  - Inbox
- AsyncAPI
- Observability
- Testing
- Configuration Reference
- Operations
- API Reference (auto-generated)
```

### Page Descriptions

#### 1. Introduction (`index.md`)
- What Ratatoskr is and why it exists
- Core philosophy: CloudEvents-native, channel-first, durability as a first-class concern
- Package overview table (Ratatoskr, Ratatoskr.EfCore, Ratatoskr.RabbitMq, Ratatoskr.Testing)
- When to use / when not to use
- Links to getting started

#### 2. Getting Started (`getting-started.md`)
- Prerequisites (.NET 10, a message broker or database)
- Step-by-step: install packages, define a message, create a handler, configure channels, publish, consume
- Minimal working example that runs end-to-end
- "What's next" pointers to deeper topics

#### 3. Architecture (`architecture.md`)
- End-to-end message flow with mermaid diagrams
- Publishing pipeline: serialize → route → intercept → send
- Consuming pipeline: receive → deserialize → dispatch → handle
- Where inbox/outbox fit in the pipeline
- Extension points (interceptors, filters, observers)
- Design principles (no magic, explicit configuration, transport-agnostic core)

#### 4. CloudEvents (`cloudevents.md`)
- What CloudEvents are and why Ratatoskr uses them
- How message properties map to CloudEvents attributes
- Binary vs structured content mode (`ConfigureCloudEvents()`)
- Extension attributes and custom headers
- AMQP binary mode mapping (RabbitMQ)
- Schema evolution guidance

#### 5. Messages & Handlers (`messages-handlers.md`)
- Defining messages with `[RatatoskrMessage("type")]`
- `IMessageHandler<T>` interface
- Handler registration: `AddHandler<T>()` vs `AddHandler("key", ...)`
- Handler keys and `[HandlerKey("...")]` attribute
- Fire-and-forget vs inbox-managed handlers
- MessageProperties: accessing CloudEvents metadata in handlers
- Handler filters (`IHandlerFilter`)

#### 6. Channels & Routing (`channels-routing.md`)
- Channel-first design philosophy
- Event vs command channels (publish/consume)
- Ownership rules: one publisher, many consumers (events) vs one consumer (commands)
- Configuring channels: `AddEventPublishChannel`, `AddCommandConsumeChannel`, etc.
- Message type registration on channels (`.Publishes<T>()`, `.Consumes<T>()`)
- Routing: how messages find their channels
- Route interceptors (`IMessageRouteInterceptor`)

#### 7. RabbitMQ Transport (`rabbitmq.md`)
- Setup: `UseRabbitMq()` and `WithRabbitMq()` on channels
- Connection configuration
- Exchange types: Topic, Direct, Fanout
- Queue types: Quorum (default) vs Classic
- Automatic topology management
- Retry queues with TTL and dead-letter queues
- Publisher confirms
- Prefetch count and consumer configuration
- Health checks: `RabbitMqConsumerHealthCheck` registration and recommended setup
- RabbitMQ-specific topology diagram (mermaid)

#### 8. EF Core Transport (`efcore-transport.md`)
- When to use: in-process durable delivery without a broker
- Setup: `WithEfCore()` on channels
- How it works: writes directly to inbox tables
- Same-DbContext optimization (single transaction)
- Comparison with RabbitMQ transport (trade-offs)
- Multi-DbContext bounded context isolation

#### 9. Outbox Pattern (`outbox.md`)
- Problem: dual-write between database and message broker
- How the outbox solves it (transactional staging → background dispatch)
- Setup: `AddEfCoreDurability<T>(d => d.UseOutbox())`, `RegisterOutbox<T>(sp)`, `AddOutboxEntities()`
- Using the outbox: `OutboxMessages.Add()` + `SaveChangesAsync()`
- Configuration options: polling interval, batch size, retry, stuck message threshold, send timeout, max message size
- Outbox processing lifecycle and error handling
- Poisoned messages and concurrency tokens
- Data retention and cleanup (automatic configuration; manual SQL in Operations)

#### 10. Inbox Pattern (`inbox.md`)
- Problem: at-least-once delivery means duplicate processing
- How the inbox solves it (idempotent, per-handler deduplication)
- Setup: `AddEfCoreDurability<T>(d => d.UseInbox())`, `UseInbox<T>()` on channels, `AddInboxEntities()`
- Per-message opt-in: `.Consumes<T>(m => m.UseInbox())`
- Handler isolation and independent retry
- Deduplication via `(MessageId, HandlerKey)` constraint
- Distributed locking (Medallion.Threading)
- Configuration options: max retries, retry delay, batch size, handler timeout, stuck threshold
- Processing flow with mermaid diagram
- Poisoned messages
- RabbitMQ + Inbox integration
- Multi-DbContext support
- Data retention and cleanup (automatic configuration; manual SQL in Operations)

#### 11. AsyncAPI (`asyncapi.md`)
- What AsyncAPI is and why it matters
- Automatic document generation from channel/message configuration
- Setup: `ConfigureAsyncApi()` and `app.MapAsyncApi()`
- `[AsyncApiMessage]` attribute for additional metadata
- JSON Schema generation for message payloads
- EventCatalog integration
- RabbitMQ bindings in generated documents

#### 12. Observability (`observability.md`)
- OpenTelemetry integration overview
- Tracing: ActivitySource, spans for publish/consume/dispatch/handle
- Metrics: full reference table of all ~15 instruments (counters, histograms, gauges)
- W3C trace context propagation across transports
- Setting up with .NET OpenTelemetry SDK
- Dashboard/alert recommendations
- `IMessageActivityObserver` for custom observability

#### 13. Testing (`testing.md`)
- The `Ratatoskr.Testing` package
- Setup: `AddRatatoskrTesting()`
- `MessageTrackingSession` — scoped tracking per test
- Waiting for messages: `WaitForPublished<T>()`, `WaitForDispatched<T>()`, etc.
- Asserting on messages: `Single<T>()`, `ShouldHaveMessage<T>()`, `ShouldHaveNoMessage<T>()`
- `TrackedMessage` — inspecting properties, results, exceptions, raw body
- `ActivityTracker` convenience API
- W3C trace context for parallel test isolation
- Testing with outbox (verifying staged messages)
- Testing with inbox: `WithoutBackgroundProcessing()` + `ProcessInboxAsync`
- Transport wire format assertions

#### 14. Configuration Reference (`configuration.md`)
- Quick-reference index of all configuration options with links to the relevant feature page
- Tables organized by area: core, channels, RabbitMQ, EF Core durability, CloudEvents, AsyncAPI, testing
- Attributes reference (`[RatatoskrMessage]`, `[HandlerKey]`, `[AsyncApiMessage]`)
- No duplication of detailed explanations — each entry links to the feature page that owns it

#### 15. Operations (`operations.md`)
- Monitoring: which metrics to alert on
- Investigating poisoned messages (inbox + outbox)
- Manual retry procedures
- Data retention: automatic cleanup configuration + manual SQL
- Distributed lock providers setup
- Disaster recovery scenarios
- Database-provider-specific notes (PostgreSQL, SQL Server) live here

## Why This Approach

1. **Hybrid structure**: Grouped navigation (Core Concepts, Transports, Durability) provides guided learning path, but each page is self-contained with its own setup/usage/config sections so readers can jump directly to what they need.

2. **Full restructure**: Existing pages are well-written but inconsistent in depth and style. Rewriting everything ensures consistent voice, progressive examples building on the same example project, and no gaps.

3. **Dedicated example project**: A minimal `examples/Docs/` project (no Aspire) that docs reference directly. Code stays compilable and testable, preventing snippet drift. The PlaygroundApi remains for advanced/Aspire scenarios.

4. **Provider-agnostic**: EF Core abstracts database differences. Provider-specific SQL/config only appears in the Operations page where ops teams need it.

5. **Additional topics**: CloudEvents, Messages & Handlers, Channels & Routing, and Health Checks round out the documentation. These are fundamental concepts that would leave gaps if undocumented.

## Key Decisions

- **Full rewrite** of all existing documentation pages for consistency
- **Hybrid navigation**: grouped sections with self-contained pages
- **New `examples/Docs/` project** (minimal, no Aspire) for all doc code examples
- **MassTransit/Wolverine style**: concept-focused, progressive, rich examples
- **Provider-agnostic** examples; DB-specific details only in Operations
- **All additional topics included**: CloudEvents, Messages & Handlers, Channels & Routing
- **Health checks folded into RabbitMQ page** (too thin for standalone)
- **Configuration Reference as quick-reference index** linking to feature pages, not duplicating them
- **Order processing domain** for all example code (OrderPlaced, ProcessPayment, etc.)

## Open Questions

_None — all questions resolved during brainstorming._
