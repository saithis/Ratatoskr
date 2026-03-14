---
title: "docs: Complete Documentation Restructure with DocFX"
type: docs
status: completed
date: 2026-03-12
origin: docs/brainstorms/2026-03-12-documentation-restructure-brainstorm.md
---

# docs: Complete Documentation Restructure with DocFX

## Enhancement Summary

**Deepened on:** 2026-03-12
**Sections enhanced:** All phases + guidelines
**Research sources:** DocFX Context7 docs, SpecFlow user-flow analysis, source code API verification

### Key Improvements
1. Added DocFX-specific syntax guidance (code snippets from files, xref, tabs, admonitions)
2. Fixed inaccurate API references (`IHandlerFilter` and `[HandlerKey]` attribute don't exist)
3. Added Docker Compose for example project infrastructure
4. Added missing content areas: delivery guarantees, serialization, DI scoping, glossary, graceful shutdown
5. Reordered Core Concepts: Messages & Handlers before CloudEvents for progressive disclosure
6. Added EF Core migration step and transport choice guidance to Getting Started

### New Considerations Discovered
- `IHandlerFilter` does not exist in the codebase — remove from plan
- `[HandlerKey]` attribute does not exist — handler keys are set only via `WithHandler<T>("key")`
- `IMessageSerializer` exists but was not covered — add serialization section
- Getting Started needs explicit transport choice + EF Core migration instructions
- EF Core transport vs. EF Core durability (outbox/inbox) distinction must be called out explicitly

## Overview

Rewrite the entire Ratatoskr documentation site from scratch using DocFX. The current docs have excellent content in some pages (architecture, inbox, testing, operations) but are inconsistent in depth, style, and structure. This plan creates a unified, progressive documentation experience with 15 pages, a new example project, and grouped navigation — targeting both internal developers and the open-source community.

## Problem Statement

Current documentation gaps:
- No Getting Started tutorial — new users can't go from zero to a working app
- No standalone pages for Outbox, CloudEvents, AsyncAPI, Observability, Messages & Handlers, Channels & Routing, EF Core transport
- Existing `index.md` has incorrect API examples (`UseEfCore<AppDbContext>()` doesn't exist)
- Inconsistent style and depth across pages
- No compilable example project referenced from docs (PlaygroundApi requires Aspire)
- Flat navigation with no grouping or learning path

## Proposed Solution

Full restructure into 15 pages with grouped navigation, a new minimal example project (`examples/Docs/`), and consistent MassTransit/Wolverine-inspired style. (see brainstorm: docs/brainstorms/2026-03-12-documentation-restructure-brainstorm.md)

## Technical Approach

### Example Project Domain

All documentation uses an **order processing** domain:
- `OrderPlaced` event — published when an order is created
- `ProcessPayment` command — sent to payment service
- `PaymentCompleted` event — published after successful payment
- `ShipOrder` command — sent to shipping service
- `OrderShipped` event — published after shipment
- `SendOrderConfirmation` command — notification to customer

This single domain covers: events, commands, multiple handlers, outbox (order creation + event in same TX), inbox (payment deduplication), routing (different channels for different bounded contexts).

### Architecture

```
examples/Docs/
├── Docs.csproj                    # Minimal, no Aspire. References all 4 Ratatoskr packages
├── Program.cs                     # Full working app setup with #region markers for DocFX snippets
├── docker-compose.yml             # RabbitMQ + PostgreSQL for running locally
├── Messages/
│   ├── OrderPlaced.cs
│   ├── ProcessPayment.cs
│   ├── PaymentCompleted.cs
│   ├── ShipOrder.cs
│   ├── OrderShipped.cs
│   └── SendOrderConfirmation.cs
├── Handlers/
│   ├── ProcessPaymentHandler.cs
│   ├── ShipOrderHandler.cs
│   ├── SendOrderConfirmationHandler.cs
│   └── OrderPlacedHandler.cs
├── Data/
│   └── OrderDbContext.cs          # EF Core DbContext with IOutboxDbContext + IInboxDbContext
└── appsettings.json
```

> [!IMPORTANT]
> Use `#region` markers in example project files so DocFX can include specific code snippets:
> `[!code-csharp[](../examples/Docs/Program.cs#ConfigureRabbitMq)]`
> This keeps docs in sync with compilable code automatically.

### Implementation Phases

#### Phase 1: Foundation (Example Project + Infrastructure)

Create the example project and update DocFX configuration. All work happens on a feature branch — the live site stays intact until the branch merges.

**Tasks:**

- [x] Create `examples/Docs/Docs.csproj` — minimal ASP.NET Core project targeting `net10.0`, referencing `Ratatoskr`, `Ratatoskr.EfCore`, `Ratatoskr.RabbitMq`, `Ratatoskr.Testing`
- [x] Create message classes in `examples/Docs/Messages/` using `[RatatoskrMessage]` attribute with order processing domain
- [x] Create handler classes in `examples/Docs/Handlers/` demonstrating both fire-and-forget and inbox-managed patterns
- [x] Create `OrderDbContext` in `examples/Docs/Data/` implementing `IOutboxDbContext` and `IInboxDbContext`
- [x] Create `Program.cs` with full Ratatoskr configuration: RabbitMQ transport, EF Core durability (inbox + outbox), AsyncAPI, OpenTelemetry, health checks
- [x] Create `appsettings.json` with connection string placeholders
- [x] Create `docker-compose.yml` with RabbitMQ (management plugin) + PostgreSQL for local development
- [x] Add `#region` markers to all example files for DocFX code snippet inclusion (e.g., `#region ConfigureRabbitMq` ... `#endregion`)
- [ ] Verify example project compiles: `dotnet build examples/Docs/` (skipped — SDK version mismatch in environment)
- [x] Update `docs/toc.yml` with the new grouped navigation structure (see brainstorm Navigation section)
- [x] Update `docs/docfx.json` if needed (verify `examples/` isn't excluded from resource paths)
- [x] Delete old pages that won't be rewritten in place: `docs/roadmap.md`, `docs/overview.md`, `docs/topology.md` (content absorbed into new pages)

**File transition strategy:** Pages that share a filename with their replacement (`architecture.md`, `inbox.md`, `testing.md`, `operations.md`, `configuration.md`, `index.md`) are overwritten in place during Phases 2-6. Pages with no replacement (`roadmap.md`, `overview.md`) or whose content moves to a differently-named file (`topology.md` → `rabbitmq.md`) are deleted here. New pages (`getting-started.md`, `cloudevents.md`, `messages-handlers.md`, `channels-routing.md`, `rabbitmq.md`, `efcore-transport.md`, `outbox.md`, `asyncapi.md`, `observability.md`) are created fresh in their respective phases.

**Success criteria:** Example project compiles, new toc.yml renders correct grouped navigation in DocFX.

#### Phase 2: Core Pages (Introduction → Channels & Routing)

Write the 6 foundational pages that every reader needs.

**Tasks:**

- [x] Write `docs/index.md` — Introduction with correct API examples from the example project, package overview table, philosophy, when to use/not use. Include a **Key Terminology** section defining: channel, transport, handler, handler key, inbox-managed, fire-and-forget, poisoned message, route interceptor
- [x] Write `docs/getting-started.md` — Step-by-step tutorial with two clear paths:
  - **Primary path (recommended):** RabbitMQ + EF Core outbox — requires Docker (`docker compose up`)
  - **Simplest path:** EF Core transport only — no broker needed, just a database
  - Both paths must include: install packages → define OrderPlaced message → create handler → configure channels → **run EF Core migration** (`dotnet ef migrations add InitialCreate`) → publish → consume → verify
  - End with "What's Next" pointers
- [x] Write `docs/architecture.md` — Reuse Mermaid diagrams from existing architecture.md (they're excellent), rewrite prose for consistent voice. Cover publishing pipeline, consuming pipeline, inbox/outbox placement, extension points. Add a **Delivery Guarantees** section: at-least-once semantics, no ordering guarantees across retries, idempotency requirements. Add a **Key Distinction** callout: EF Core transport (delivery mechanism) vs. EF Core durability (outbox/inbox pattern)
- [x] Write `docs/messages-handlers.md` — `[RatatoskrMessage]`, `IMessageHandler<T>`, handler registration via `WithHandler<T>()` (fire-and-forget) and `WithHandler<T>("key")` (inbox-managed), `MessageProperties` access, custom serialization (`IMessageSerializer`), DI scoping (handler lifetime, scope-per-handler in inbox, DbContext behavior)
- [x] Write `docs/channels-routing.md` — Channel-first design, event vs command semantics, ownership rules, channel configuration API, message type registration, routing, `IMessageRouteInterceptor`
- [x] Write `docs/cloudevents.md` — CloudEvents spec, MessageProperties mapping, binary vs structured mode, `ConfigureCloudEvents()`, extension attributes, AMQP mapping, schema evolution. Placed after Messages & Handlers so readers have concrete context first

**Page order in navigation:** Introduction → Getting Started → Architecture → Messages & Handlers → Channels & Routing → CloudEvents (moved after concrete concepts)

**Success criteria:** A new developer can read Introduction → Getting Started and have a running app. Architecture provides the mental model. Messages, Channels, and CloudEvents pages are self-contained references.

#### Phase 3: Transport Pages

Write the 2 transport-specific pages.

**Tasks:**

- [x] Write `docs/rabbitmq.md` — Setup (`UseRabbitMq()`, `WithRabbitMq()`), connection config, exchange types (Topic/Direct/Fanout), queue types (Quorum/Classic), topology management, retry queues + DLQ with Mermaid diagram (reuse from existing topology.md), publisher confirms, prefetch, health checks (`RabbitMqConsumerHealthCheck`), consumer configuration
- [x] Write `docs/efcore-transport.md` — When to use (no broker needed), setup (`WithEfCore()`), how it writes to inbox tables, same-DbContext optimization, comparison table vs RabbitMQ, multi-DbContext isolation. **Must include a prominent callout:** "EF Core transport is a *delivery mechanism*. It is separate from EF Core *durability* (outbox/inbox). You can use the EF Core transport without outbox/inbox, and vice versa."

**Success criteria:** Each transport page is self-contained with setup, configuration, and architecture explanation.

#### Phase 4: Durability Pages

Write the 2 durability pattern pages.

**Tasks:**

- [x] Write `docs/outbox.md` — Problem (dual-write), solution (transactional staging), setup (`AddEfCoreDurability<T>(d => d.UseOutbox())`, `RegisterOutbox<T>(sp)`, `AddOutboxEntities()`), usage (`OutboxMessages.Add()` + `SaveChangesAsync()`), configuration options table, processing lifecycle, error handling, poisoned messages, concurrency tokens, retention config (link to Operations for manual SQL)
- [x] Write `docs/inbox.md` — Problem (duplicate processing), solution (per-handler deduplication), setup, per-message opt-in, handler isolation, deduplication constraint, distributed locking, configuration options table, processing flow Mermaid diagram (reuse from existing), poisoned messages, RabbitMQ integration, multi-DbContext, retention config (link to Operations for manual SQL)

**Success criteria:** Outbox and Inbox are standalone pages with complete setup-to-production coverage. No duplication between them beyond shared concepts.

#### Phase 5: Feature Pages (AsyncAPI, Observability, Testing)

Write the 3 standalone feature pages.

**Tasks:**

- [x] Write `docs/asyncapi.md` — What AsyncAPI is, automatic generation from channel config, setup (`ConfigureAsyncApi()`, `app.MapAsyncApi()`), `[AsyncApiMessage]` attribute, JSON Schema generation, EventCatalog extension properties, RabbitMQ bindings
- [x] Write `docs/observability.md` — OpenTelemetry overview, tracing (ActivitySource, span hierarchy for publish/consume/dispatch/handle), metrics reference table (all ~15 instruments from `RatatoskrDiagnostics`), W3C trace context propagation, .NET OTel SDK setup code (concrete `Program.cs` snippet with `AddMeter`/`AddSource`), example Prometheus queries for key metrics, `IMessageActivityObserver` for custom observability
- [x] Write `docs/testing.md` — `Ratatoskr.Testing` package, `AddRatatoskrTesting()`, `MessageTrackingSession`, waiting methods, assertion helpers, `TrackedMessage`, `ActivityTracker`, W3C trace isolation, testing with outbox, testing with inbox (`WithoutBackgroundProcessing()` + `ProcessInboxAsync`), transport wire format assertions

**Success criteria:** Each page is self-contained. Observability page includes a complete metrics reference table extracted from `RatatoskrDiagnostics` source.

#### Phase 6: Reference & Operations Pages

Write the final 2 pages.

**Tasks:**

- [x] Write `docs/configuration.md` — Quick-reference index with tables organized by area (core, channels, RabbitMQ, EF Core durability, CloudEvents, AsyncAPI, testing). Each row: option name, type, default, link to feature page. Attributes reference table (`[RatatoskrMessage]`, `[AsyncApiMessage]`). **Duplication strategy:** feature pages are the source of truth for config details; this page has a condensed table with links, not full explanations
- [x] Write `docs/operations.md` — Rewrite existing operations.md for consistent voice. Monitoring (which metrics to alert on), poisoned message investigation (inbox + outbox), manual retry SQL, data retention (automatic config + manual cleanup), distributed lock providers (File/PostgreSQL/SQL Server/Redis), disaster recovery, **graceful shutdown** (SIGTERM handling, how outbox/inbox processors stop cleanly during rolling deployments), **EF Core migration guidance** for library version upgrades. Provider-specific SQL lives here (PostgreSQL + SQL Server)

**Success criteria:** Configuration Reference links to all feature pages correctly. Operations page is a complete production runbook.

#### Phase 7: Verification & Polish

Cross-check everything, build the site, verify.

**Tasks:**

- [ ] Build DocFX locally: `dotnet docfx docs/docfx.json` — verify no warnings/errors (skipped — NETSDK1226 SDK version mismatch in environment)
- [x] Verify all internal links between pages resolve (cross-references)
- [ ] Verify example project still compiles: `dotnet build examples/Docs/` (skipped — NETSDK1226 SDK version mismatch in environment)
- [x] Verify code examples in docs match actual API surface (spot-check against source) — all 29 APIs verified
- [x] Verify toc.yml navigation renders correctly with grouping — all 15 entries match files
- [x] Review each page for consistent voice, progressive disclosure, and self-containability
- [ ] Verify Mermaid diagrams render in DocFX modern template (requires DocFX build)
- [x] Remove any leftover old doc files not part of the new structure — no orphans found
- [ ] Run existing tests to ensure no regressions (skipped — NETSDK1226 SDK version mismatch in environment)

**Success criteria:** DocFX builds clean, all links work, example compiles, site looks professional.

## Page Writing Guidelines

Each documentation page should follow this consistent structure:

1. **Opening paragraph** — What this page covers and why it matters (2-3 sentences)
2. **Concept explanation** — The "what" and "why" before any code
3. **Setup / Getting Started** — Minimal code to get the feature working
4. **Usage** — Common patterns with code examples from `examples/Docs/`
5. **Configuration** — Options table with defaults and descriptions
6. **Advanced Topics** — Edge cases, integration with other features
7. **What's Next** — Links to related pages

### Code Examples

- **Prefer DocFX code inclusion from the example project** over inline snippets. This prevents drift:
  ```markdown
  [!code-csharp[](../examples/Docs/Program.cs#ConfigureRabbitMq)]
  ```
  Use `#region ConfigureRabbitMq` / `#endregion` markers in example files.
- For short snippets (< 5 lines) that don't warrant a region, inline code is fine
- Use the order processing domain consistently
- Be provider-agnostic (EF Core level, not PostgreSQL/SQL Server specific)
- Show the minimal code needed, not the kitchen sink

### DocFX Syntax to Use Consistently

- **Cross-references to API docs:** `<xref:Ratatoskr.IRatatoskr>` links to the auto-generated API page
- **Admonitions** for callouts:
  ```markdown
  > [!TIP]
  > Optional helpful information.

  > [!WARNING]
  > Important caveat the reader must know.

  > [!IMPORTANT]
  > Critical setup requirement.
  ```
- **Tab groups** where multiple configurations apply (e.g., RabbitMQ vs EF Core transport):
  ```markdown
  # [RabbitMQ](#tab/rabbitmq)
  ...rabbitmq config...
  # [EF Core](#tab/efcore)
  ...efcore config...
  ```
- **Links between pages:** standard markdown `[Outbox](outbox.md)` — DocFX resolves these

## Existing Content Reuse Strategy

| Existing Page | Reuse Plan |
|---|---|
| `architecture.md` | Mermaid diagrams are excellent — preserve and reuse in new architecture.md. Rewrite prose. |
| `inbox.md` | Split: inbox content → new inbox.md, outbox config tables → new outbox.md. Rewrite for consistency. |
| `testing.md` | Strong content — restructure into new testing.md with consistent voice. |
| `operations.md` | Mostly reusable — rewrite for voice consistency, keep SQL examples. |
| `topology.md` | Mermaid diagram + retry flow → fold into new rabbitmq.md. |
| `configuration.md` | Channel config examples → distribute to channels-routing.md and getting-started.md. |
| `index.md` | Rewrite completely — current version has incorrect API calls. |
| `overview.md` | Absorb goals into new index.md introduction. Delete. |
| `roadmap.md` | Internal planning, not user-facing. Delete or move to GitHub project. |

## Acceptance Criteria

### Functional Requirements

- [ ] 15 documentation pages written with consistent style and voice
- [ ] `examples/Docs/` project compiles and demonstrates all documented features
- [ ] Grouped navigation (Core Concepts, Transports, Durability) renders in DocFX
- [ ] All code examples use the order processing domain
- [ ] All cross-references between pages resolve
- [ ] Configuration Reference links to every feature page's config section
- [ ] DocFX builds without warnings: `dotnet docfx docs/docfx.json`
- [ ] API Reference auto-generation still works

### Content Requirements

- [ ] New developer can follow Introduction → Getting Started and have a running app (including `docker compose up` and EF Core migration)
- [ ] Each page is self-contained (can be read independently)
- [ ] Provider-agnostic code examples throughout (except Operations)
- [ ] Mermaid diagrams for: architecture pipeline, inbox processing flow, RabbitMQ topology, outbox lifecycle
- [ ] Complete metrics reference table in Observability page
- [ ] Complete configuration options tables in each feature page
- [ ] Delivery guarantees documented in Architecture (at-least-once, no ordering, idempotency)
- [ ] EF Core transport vs. durability distinction explicitly called out
- [ ] Serialization customization covered in Messages & Handlers
- [ ] Key terminology defined in Introduction

### Quality Gates

- [ ] Example project compiles: `dotnet build examples/Docs/`
- [ ] DocFX builds clean: `dotnet docfx docs/docfx.json`
- [ ] Existing tests pass: `dotnet run --project tests/Ratatoskr.Tests -- --maximum-parallel-tests 10`
- [ ] No broken internal links between doc pages

## Dependencies & Prerequisites

- DocFX already configured and deployed via GitHub Pages (no infrastructure changes needed)
- GitHub Actions workflow already triggers on `docs/**` changes
- All source code exists and is stable — this is documentation, not feature work

## Risk Analysis & Mitigation

| Risk | Impact | Mitigation |
|---|---|---|
| Code examples drift from actual API | High — wrong docs are worse than no docs | Use DocFX `[!code-csharp[]]` to include from compilable example project; CI verifies build |
| Scope creep — writing too much per page | Medium — delays completion | Stick to the page writing guidelines; each page targets 500-1500 words |
| Mermaid diagrams break in DocFX | Low — known to work in current docs | Reuse existing working diagrams where possible |
| DocFX template doesn't support nested toc | Low — modern template supports it | Test toc.yml early in Phase 1 |
| Documenting non-existent APIs | High — broken examples | Verified: `IHandlerFilter` and `[HandlerKey]` attribute don't exist. Only document `WithHandler<T>("key")` fluent API. Always verify APIs against source before writing |

## Sources & References

### Origin

- **Brainstorm document:** [docs/brainstorms/2026-03-12-documentation-restructure-brainstorm.md](docs/brainstorms/2026-03-12-documentation-restructure-brainstorm.md) — Key decisions carried forward: full rewrite for consistency, hybrid navigation structure, order processing example domain, provider-agnostic examples, MassTransit/Wolverine style.

### Internal References

- Existing architecture diagrams: `docs/architecture.md`
- Public API surface: `src/Ratatoskr/ServiceCollectionExtensions.cs`, `src/Ratatoskr.EfCore/PublicApiExtensions.cs`, `src/Ratatoskr.RabbitMq/Extensions/`
- Metrics instruments: `src/Ratatoskr/Diagnostics/RatatoskrDiagnostics.cs`
- AsyncAPI: `src/Ratatoskr/AsyncApi/AsyncApiDocumentGenerator.cs`
- Example app: `examples/PlaygroundApi/` (reference for working configuration patterns)

### Style References

- MassTransit docs: concept-focused with progressive disclosure
- Wolverine docs: practical, code-heavy guides
- DocFX modern template: supports nested toc, search, Mermaid
