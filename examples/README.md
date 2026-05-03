# Ratatoskr examples playground

Single ASP.NET Core host (**PlaygroundHost**) plus Aspire **AppHost** demonstrates the main Ratatoskr building blocks: EF Core outbox/inbox on two logical PostgreSQL databases (publisher vs consumer roles), RabbitMQ fan-out and retries, a **playground** HTTP surface (toggles, activities, diagnostics), a **server-driven scenario runner** (catalog, run status, cancel), and the static dashboard under `examples/PlaygroundHost/wwwroot/`.

`examples/Docs` remains a **docfx-only** snippet project; it is not part of the runnable playground.

## Topology

| Piece | Role |
|---|---|
| **PlaygroundHost** | Ratatoskr bus, all handlers, order + playground HTTP APIs, scenario runner, static UI, Rabbit depth probe |
| **publisherdb** | Publisher `DbContext`: order row, outbox for cross-service messages, inbox for outcomes, EF Core internal `orders.internal` channel |
| **consumerdb** | Consumer `DbContext`: command inbox + outcome outbox |
| **playgrounddb** | Scenario run ledger (`Runs` table) |
| **RabbitMQ** | `ecommerce.events` / `ecommerce.commands` topology (two logical consume channel names can share one AMQP exchange) |

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
| `Playground:SingleFlight` | When true, only one scenario run at a time (HTTP 400 if another is in progress). |
| `Playground:RegisterBlockingScenario=1` | Registers `blocking-hold` scenario (long sleep) for manual single-flight experiments. |
| `Playground:RegisterCancelSmokeScenario=1` | Registers `cancel-smoke` scenario (waits until cancelled) for tests. |

## Feature coverage (where)

| Feature | Where |
|---|---|
| Outbox (multi-message one transaction) | `POST /api/orders` — scenario `outbox-success` and related slugs |
| Outbox transport failure injection | Toggle `simulate-outbox-transport-failure` + `OutboxFailureState` (run-scoped via CloudEvents extension when configured in scenarios) |
| Outbox max message size | `WithMaxMessageSize` on publisher outbox; `POST /api/orders/oversized`; scenario `oversized-payload-rolls-back` |
| EF Core internal channel | `orders.internal` + `ReserveStockInternal`; scenario `efcore-internal-command` |
| Direct publish | `POST /api/orders/direct`; scenario `direct-consume-success` |
| Replay | `POST /api/orders/{id}/replay`; scenario `replay-dedups` |
| Fan-out (two handlers, one queue) | Notification handlers on `ecommerce.events.notifications`; scenario `fanout-two-handlers-on-orderplaced` |
| Inbox retries / poison | Inventory throw / succeed-after; scenarios `inbox-poison`, `inbox-retry-then-success` |
| Rabbit retry + DLQ | Managed consumer queues; scenario `direct-consume-dlq` |
| Management API + requeue | `MapRatatoskrManagementApi` — paths under `ratatoskr/api/v1/efcore/contexts/{PublisherDbContext\|ConsumerDbContext}/...` |
| Diagnostics summary | `GET /api/playground/diagnostics/poisoned-summary` (publisher vs consumer poisoned counts) |
| Activity log | `PlaygroundActivityRecorder` — `GET /api/playground/activities?orderId=` or `?scenarioRunId=` |

## Scenario catalog

Scenarios are **server-side** (`GET /api/playground/scenarios`, `POST /api/playground/scenarios/{slug}/run`, `GET /api/playground/runs/{id}`, `POST /api/playground/runs/{id}/cancel`). The dashboard loads the catalog from the server (no duplicate JSON in `wwwroot`).

Slug examples: `outbox-success`, `outbox-retry-then-success`, `outbox-poison`, `inbox-retry-then-success`, `inbox-poison`, `business-rejection`, `direct-consume-success`, `direct-consume-retry`, `direct-consume-dlq`, `replay-dedups`, `efcore-internal-command`, `fanout-two-handlers-on-orderplaced`, `oversized-payload-rolls-back`.

## Project layout

```
examples/
  AppHost/           Aspire — postgres (publisherdb, consumerdb, playgrounddb) + rabbit + PlaygroundHost
  PlaygroundHost/    Single demo host + wwwroot dashboard
  ServiceDefaults/   Shared OpenTelemetry + health
  Docs/              Docfx snippets only
```

## Tests

HTTP-first playground tests live in `tests/PlaygroundHost.Tests` (separate assembly so `WebApplicationFactory<Program>` does not collide with `Ratatoskr.TestHost`). Library-level Ratatoskr integration tests remain in `tests/Ratatoskr.Tests`.
