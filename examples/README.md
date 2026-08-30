# Ratatoskr examples playground

Two ASP.NET Core hosts plus an Aspire **AppHost**. **PlaygroundHost** demonstrates the Ratatoskr building blocks: EF Core outbox/inbox on two logical PostgreSQL databases (publisher vs consumer), RabbitMQ fan-out and retries, a **playground** HTTP surface (activities, diagnostics, Rabbit queue depths), a **server-driven scenario runner** (catalog, run status, cancel), and the static dashboard under `examples/PlaygroundHost/wwwroot/`. **InventoryService** is a second Ratatoskr service that exists to show the [management dashboard](../docs/management-ui.md) aggregating more than one service and more than one `DbContext`.

Each **scenario** is a fixed script with its own **wire types** (`[RatatoskrMessage("{slug}.{kind}")]` style names) and **per-slug Rabbit topology** (`pg.{slug}.events`, `pg.{slug}.commands`, and queues such as `pg.{slug}.orders`) so concurrent runs do not share retry or DLQ mailboxes. Scenarios register only the channels, message CLR types, and handlers their script actually uses (for example `direct-consume-dlq` wires only `OrderPlaced` on the notifications queue). There are no global playground toggles; failure paths are encoded in the scenario handlers or run-scoped helpers (for example outbox send simulation).

`examples/Docs` remains a **docfx-only** snippet project; it is not part of the runnable playground.

## Topology

| Piece | Role |
|---|---|
| **PlaygroundHost** | Ratatoskr bus, handlers, minimal HTTP APIs, scenario runner, static UI, Rabbit depth probe, **Ratatoskr management dashboard** at `/ratatoskr` |
| **publisherdb** | Publisher `DbContext`: order row where scenarios need it, outbox, inbox, EF Core internal channel where registered |
| **consumerdb** | Consumer `DbContext`: command inbox + outcome outbox |
| **playgrounddb** | Scenario run ledger (`Runs` table) |
| **RabbitMQ** | Per-scenario exchanges and queues derived from slug (see `PlaygroundAmqpNames`) |
| **InventoryService** | Second Ratatoskr service. Management API only, no dashboard of its own, no broker: it uses the EF Core transport |
| **inventorydb** | `InventoryDbContext`: outbox **and** inbox |
| **auditdb** | `AuditDbContext`: outbox **only**, so the dashboard shows a half-configured context |

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
| Outbox (publisher stages command; consumer returns outcome event) | Scenarios `outbox-success`, `outbox-retry-then-success` |
| Outbox (multi-message one transaction) | Scenario `efcore-internal-command` (two internal rows in one `SaveChanges`) |
| Outbox transport failure until poison | Scenario `outbox-retry-then-success`, `outbox-poison` (run-scoped outbox send registry) |
| Outbox max message size | `WithMaxMessageSize` on publisher outbox; scenario `oversized-payload-rolls-back` |
| EF Core internal channel | Scenario `efcore-internal-command` |
| Direct publish / consume | Scenarios `direct-consume-success`, `direct-consume-retry`, `direct-consume-dlq` |
| Replay deduplication | Scenario `replay-dedups` (two `PublishDirectAsync` calls with the same id: `UseInbox` consumer runs once, `AllowConsumeWithoutInbox` consumer runs twice) |
| Fan-out (two handlers, one queue) | Scenario `fanout-two-handlers-on-orderplaced` |
| Inbox retries / poison | Scenarios `inbox-poison`, `inbox-retry-then-success` |
| Business rejection path | Scenario `business-rejection` |
| Management API + requeue | `MapRatatoskrManagementApi` — paths under `ratatoskr/api/v1/efcore/contexts/{PublisherDbContext\|ConsumerDbContext}/...` |
| Management dashboard | `MapRatatoskrUI("/ratatoskr")` on PlaygroundHost |
| Dashboard across multiple DbContexts | PlaygroundHost lists `PublisherDbContext` + `ConsumerDbContext`; InventoryService lists `InventoryDbContext` + `AuditDbContext` |
| Dashboard across multiple services | `AddRatatoskrUI(ui => ui.AddService("Inventory Service", ...))` in `PlaygroundHost/Program.cs` |
| Diagnostics summary | `GET /api/playground/diagnostics/poisoned-summary` |
| Activity log | `PlaygroundActivityRecorder` — `GET /api/playground/activities?orderId=` or `?scenarioRunId=` |

## Scenario catalog

Scenarios are **server-side** (`GET /api/playground/scenarios`, `POST /api/playground/scenarios/{slug}/run`, `GET /api/playground/runs/{id}`, `POST /api/playground/runs/{id}/cancel`). The dashboard loads the catalog from the server (no duplicate JSON in `wwwroot`).

Implementation lives under `examples/PlaygroundHost/Scenarios/{Topic}/{slug}/` (messages, handlers, `*Scenario.cs`). Each scenario class implements `IPlaygroundScenario` with `RegisterRatatoskrTopology(RatatoskrBuilder)` and optional `RabbitDepthQueues` for `/api/playground/rabbit-depths`. All scenario types are listed once in `PlaygroundScenarioManifest` (`PlaygroundScenarioManifest.cs`) via typed `Entry<T>()` entries; `RegisterScenarioTopologies` and `RegisterScenarioServices` are called from `Program.cs` during `AddRatatoskr` and service registration respectively.

Slug examples: `outbox-success`, `outbox-retry-then-success`, `outbox-poison`, `inbox-retry-then-success`, `inbox-poison`, `business-rejection`, `direct-consume-success`, `direct-consume-retry`, `direct-consume-dlq`, `replay-dedups`, `efcore-internal-command`, `fanout-two-handlers-on-orderplaced`, `oversized-payload-rolls-back`, `blocking-hold`, `cancel-smoke`.

## Project layout

```
examples/
  AppHost/           Aspire — postgres (publisherdb, consumerdb, playgrounddb, inventorydb, auditdb)
                     + rabbit + PlaygroundHost + InventoryService
  PlaygroundHost/    Demo host + wwwroot dashboard + Scenarios/ + Ratatoskr management dashboard
  InventoryService/  Second Ratatoskr service, management API only (aggregated by the dashboard)
  ServiceDefaults/   Shared OpenTelemetry + health
  Docs/              Docfx snippets only
```

## Ratatoskr management dashboard

PlaygroundHost serves the embedded dashboard at **`/ratatoskr`** (mapped with the local-only
`DevOnlyNoAuth` policy). It is not the playground's own scenario UI at `/` — it is the
`Ratatoskr.UI` package, and the playground is what it happens to be pointed at.

**Multiple DbContexts.** The Poison Workbench shows one card per `DbContext` of the selected
service, each with its poisoned and pending backlog, and switches the table between them.
PlaygroundHost contributes `PublisherDbContext` and `ConsumerDbContext`; InventoryService
contributes `InventoryDbContext` (outbox + inbox) and `AuditDbContext` (outbox only, so its
Inbox toggle is disabled and its card reads "Inbox: not configured").

**Multiple services.** AppHost passes the inventory service endpoint to PlaygroundHost as
`InventoryService__Url`, which registers it with the dashboard:

```csharp
// examples/PlaygroundHost/Program.cs
builder.Services.AddRatatoskrUI(ui =>
{
    ui.Title = "Ratatoskr Playground";
    ui.LocalServiceName = "Playground Host";
    ui.AddService("Inventory Service", new Uri(inventoryServiceUrl));
});
```

The **Services** tab lists both with their health, DbContexts, and poisoned totals; the header
picker switches every tab between them. The browser only ever talks to PlaygroundHost — remote
calls are relayed through `/ratatoskr/ui-api/proxy/{service}/...`, so InventoryService needs no
CORS and no UI package.

**Producing something to look at.** InventoryService reserves stock through its own EF Core
transport, and a SKU starting with `FAIL` makes the handler throw until the inbox poisons the row:

```bash
# succeeds; also stages an audit event in AuditDbContext's outbox
curl -X POST http://localhost:<inventory-port>/inventory/reservations   -H 'content-type: application/json' -d '{"sku":"WIDGET-1","quantity":2}'

# poisons an InventoryDbContext inbox row after its retries are spent
curl -X POST http://localhost:<inventory-port>/inventory/reservations   -H 'content-type: application/json' -d '{"sku":"FAIL-1","quantity":1}'
```

Then open `/ratatoskr` on PlaygroundHost, pick **Inventory Service** in the header, and the row
shows up in the workbench where it can be inspected, requeued, or deleted.

## Tests

HTTP integration coverage for the playground host lives in **`tests/Ratatoskr.Tests`** (`Examples/PlaygroundHostScenarioHttpTests.cs`), using `WebApplicationFactory<PlaygroundHost.PlaygroundHostAppMarker>` together with the shared RabbitMQ and PostgreSQL Testcontainers fixtures. Library-level Ratatoskr integration tests remain in the same project under `Integration/`.
