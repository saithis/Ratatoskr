# Ratatoskr examples playground

Single ASP.NET Core host (**PlaygroundHost**) plus Aspire **AppHost** demonstrates Ratatoskr building blocks: EF Core outbox/inbox on two logical PostgreSQL databases (publisher vs consumer), RabbitMQ fan-out and retries, a **playground** HTTP surface (activities, diagnostics, Rabbit queue depths), a **server-driven scenario runner** (catalog, run status, cancel), and the static dashboard under `examples/PlaygroundHost/wwwroot/`.

Each **scenario** is a fixed script with its own **wire types** (`[RatatoskrMessage("{slug}.{kind}")]` style names) and **per-slug Rabbit topology** (`pg.{slug}.events`, `pg.{slug}.commands`, and queues such as `pg.{slug}.orders`) so concurrent runs do not share retry or DLQ mailboxes. There are no global playground toggles; failure paths are encoded in the scenario handlers or run-scoped helpers (for example outbox send simulation).

`examples/Docs` remains a **docfx-only** snippet project; it is not part of the runnable playground.

## Topology

| Piece | Role |
|---|---|
| **PlaygroundHost** | Ratatoskr bus, handlers, minimal HTTP APIs, scenario runner, static UI, Rabbit depth probe |
| **publisherdb** | Publisher `DbContext`: order row where scenarios need it, outbox, inbox, EF Core internal channel where registered |
| **consumerdb** | Consumer `DbContext`: command inbox + outcome outbox |
| **playgrounddb** | Scenario run ledger (`Runs` table) |
| **RabbitMQ** | Per-scenario exchanges and queues derived from slug (see `PlaygroundAmqpNames`) |

## Quick start

```bash
cd examples/AppHost
aspire run
# or: dotnet run
```

Aspire opens the dashboard (often `http://localhost:15000`). Open the **playgroundhost** HTTP endpoint for the UI. Playground APIs and scenarios respond with **404** unless the host enables the playground (`RATATOSKR_EXAMPLES_PLAYGROUND=1` is set from AppHost, or configure `Playground:Enabled` for local runs).

## Environment

| Variable / setting | Effect |
|---|---|
| `RATATOSKR_EXAMPLES_PLAYGROUND=1` | Forces `Playground:Enabled` on (used by AppHost). |
| `Playground:Enabled` | When false, `/api/playground/*` returns 404 (except static files and health). |
| `Playground:RunTimeoutSeconds` | Server-side cap for scenario execution (default 120). |

Scenarios marked **dangerous** in the catalog require `POST .../run?confirmDanger=true` (the dashboard asks for confirmation before sending this).

## Feature coverage (where)

| Feature | Where |
|---|---|
| Outbox (multi-message one transaction) | Scenario `outbox-success` and related slugs under `examples/PlaygroundHost/Scenarios/` |
| Outbox transport failure until poison | Scenario `outbox-retry-then-success`, `outbox-poison` (run-scoped outbox send registry) |
| Outbox max message size | `WithMaxMessageSize` on publisher outbox; scenario `oversized-payload-rolls-back` |
| EF Core internal channel | Scenario `efcore-internal-command` |
| Direct publish / consume | Scenarios `direct-consume-success`, `direct-consume-retry`, `direct-consume-dlq` |
| Replay deduplication | Scenario `replay-dedups` |
| Fan-out (two handlers, one queue) | Scenario `fanout-two-handlers-on-orderplaced` |
| Inbox retries / poison | Scenarios `inbox-poison`, `inbox-retry-then-success` |
| Business rejection path | Scenario `business-rejection` |
| Management API + requeue | `MapRatatoskrManagementApi` — paths under `ratatoskr/api/v1/efcore/contexts/{PublisherDbContext\|ConsumerDbContext}/...` |
| Diagnostics summary | `GET /api/playground/diagnostics/poisoned-summary` |
| Activity log | `PlaygroundActivityRecorder` — `GET /api/playground/activities?orderId=` or `?scenarioRunId=` |

## Scenario catalog

Scenarios are **server-side** (`GET /api/playground/scenarios`, `POST /api/playground/scenarios/{slug}/run`, `GET /api/playground/runs/{id}`, `POST /api/playground/runs/{id}/cancel`). The dashboard loads the catalog from the server (no duplicate JSON in `wwwroot`).

Implementation lives under `examples/PlaygroundHost/Scenarios/{Topic}/{slug}/` (messages, handlers, `*Scenario.cs`). Each scenario class implements `IPlaygroundScenario` with `RegisterRatatoskrTopology(RatatoskrBuilder)` and optional `RabbitDepthQueues` for `/api/playground/rabbit-depths`. All scenario types are listed once in `PlaygroundScenarioManifest.All` (`PlaygroundScenarioManifest.cs`); `RegisterScenarioTopologies` and `RegisterScenarioServices` are called from `Program.cs` during `AddRatatoskr` and service registration respectively.

Slug examples: `outbox-success`, `outbox-retry-then-success`, `outbox-poison`, `inbox-retry-then-success`, `inbox-poison`, `business-rejection`, `direct-consume-success`, `direct-consume-retry`, `direct-consume-dlq`, `replay-dedups`, `efcore-internal-command`, `fanout-two-handlers-on-orderplaced`, `oversized-payload-rolls-back`, `blocking-hold`, `cancel-smoke`.

## Project layout

```
examples/
  AppHost/           Aspire — postgres (publisherdb, consumerdb, playgrounddb) + rabbit + PlaygroundHost
  PlaygroundHost/    Single demo host + wwwroot dashboard + Scenarios/
  ServiceDefaults/   Shared OpenTelemetry + health
  Docs/              Docfx snippets only
```

## Tests

HTTP integration coverage for the playground host lives in **`tests/Ratatoskr.Tests`** (`Examples/PlaygroundHostScenarioHttpTests.cs`), using `WebApplicationFactory<PlaygroundHost.PlaygroundHostAppMarker>` together with the shared RabbitMQ and PostgreSQL Testcontainers fixtures. Library-level Ratatoskr integration tests remain in the same project under `Integration/`.
