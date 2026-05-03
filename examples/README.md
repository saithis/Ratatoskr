# Ratatoskr E-Commerce Playground

A multi-service example that demonstrates major Ratatoskr features in one runnable demo.

## The Scenario

A simplified order pipeline across three services:

| Service | Role |
|---|---|
| **OrderService** | Creates orders, stages `OrderPlaced`, `ProcessOrderCommand`, and broker-less `ReserveStockInternal` in one outbox transaction (or publishes direct after persisting the order); consumes `OrderFulfilled` / `OrderFailed` via inbox; optional **simulated outbox transport failures** (`IMessageSender` decorator) |
| **InventoryService** | Consumes `ProcessOrderCommand` with inbox deduplication; stages `OrderFulfilled` / `OrderFailed` through its **outbox**; playground modes: off, throw (retries/poison), **succeed-after N**, reject (`OrderFailed`) |
| **NotificationService** | **Fan-out**: two fire-and-forget handlers on the same `OrderPlaced` queue (no inbox); per-handler toggles drive **RabbitMQ** retry and DLQ |
| **Dashboard** | Hybrid UI: **scenario runner** (PASS/FAIL, expected vs actual) on top, swim-lane **timeline** for the last order, collapsible **free-form** toggles and place/replay actions, Rabbit depths, poisoned panels + requeue |

## Quick Start

Use the [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) CLI (the Aspire workload is not required):

```bash
cd examples/AppHost
aspire run
# or: dotnet run
```

The Aspire dashboard opens (for example at http://localhost:15000). Open the **dashboard** resource URL to load the playground UI. Rabbit queue depths load from the Dashboard service via `RabbitMQ.Client` passive `MessageCount` (no management HTTP API required). You can still use RabbitMQ Management for message inspection.

## Feature Coverage

| Feature | Where |
|---|---|
| Outbox (EF Core, multiple messages one transaction) | `OrderService` — `POST /api/orders` stages `OrderPlaced` + `ProcessOrderCommand` + `ReserveStockInternal` with stable CloudEvents ids (`PlaygroundMessageIds`) |
| Outbox transport failure injection | `OrderService` — playground toggle `simulate-outbox-transport-failure` wraps `IMessageSender` (`FailableMessageSender` + `OutboxFailureState`): succeed, **succeed-after N**, or always-fail (real outbox retry/poison path) |
| Outbox max message size | `OrderService` — `WithMaxMessageSize` on outbox; `POST /api/orders/oversized` fills `OrderPlaced.BulkPaddingForDemo` so `SaveChanges` rolls back (see [Outbox message size](../docs/outbox.md#message-size-validation)) |
| EF Core transport (same DbContext) | `OrderService` — internal channel `orders.internal` publishes/consumes `ReserveStockInternal` with `WithEfCore()` + inbox (see [EF Core transport](../docs/efcore-transport.md#same-dbcontext-optimization)) |
| Direct publish (no outbox) | `OrderService` — `POST /api/orders/direct` persists the order row then `PublishDirectAsync` for staged messages |
| Replay (dedup demo) | `OrderService` — `POST /api/orders/{id}/replay` republishes with the same ids |
| `[RatatoskrMessage]` attribute | All message types in `PlaygroundMessages` |
| Event publish/consume | `OrderPlaced`, `OrderFulfilled`, `OrderFailed` |
| Command send/consume | `ProcessOrderCommand` |
| Fan-out (two handlers, one queue) | `NotificationService` — `OrderPlacedNotificationHandler` + `OrderPlacedAnalyticsHandler` on `ecommerce.events.notifications` (fire-and-forget `.WithHandler<T>()`; see [Messages & handlers](../docs/messages-handlers.md#fan-out-rabbit-no-inbox)) |
| **Inbox** retries + poison | Inventory **throw** mode; OrderService consume toggles; short retry delay in tests |
| **Rabbit** retry + DLQ | Consumer queues use managed retry (`WithRetry`); DLQ `*.dlq` |
| **Direct consume** (no inbox) | `NotificationService`: handlers run on the consumer thread; **both** handlers run per delivery; if one throws, the whole delivery is nacked (no per-handler isolation without inbox) |
| Inbox deduplication | InventoryService command inbox; replay uses stable message ids |
| Management API + requeue | OrderService + InventoryService poisoned outbox/inbox |
| Outbox / inbox polling | Short intervals in examples for demo responsiveness |
| RabbitMQ transport | Cross-service messaging |
| EF Core durability | OrderService (`ordersdb`), InventoryService (`inventorydb`) |
| Playground observability | `PlaygroundActivityRecorder` (`IMessageActivityObserver`) — `GET /api/playground/activities?orderId=` |

## Dashboard scenario catalog

Scenarios are **client-side only** (no scenario-specific server APIs). Each run resets toggles, arranges state, acts (place order, replay, or call a dedicated endpoint), then polls until pass or timeout (~30–60 s). Toggle bodies support optional `mode` and `failureCount` (see below).

| ID | Topic | What it proves |
|---|---|---|
| `outbox-success` | Outbox | Happy path to **Fulfilled** |
| `outbox-retry-then-success` | Outbox | Simulated send failures then recovery |
| `outbox-poison` | Outbox | Poisoned outbox rows when send always fails |
| `inbox-success` | Inbox | Inventory inbox processes command |
| `inbox-retry-then-success` | Inbox | Inventory **succeed-after N** then **Fulfilled** |
| `inbox-poison-and-requeue` | Inbox | Poisoned inventory inbox, optional requeue narrative |
| `business-rejection` | Inbox | Inventory **reject** leads to **Failed** order |
| `direct-consume-success` | Rabbit | Notifications succeed |
| `direct-consume-retry-then-success` | Rabbit | Notification **succeed-after N** |
| `direct-consume-dlq` | Rabbit | Notification fail-until DLQ |
| `replay-dedups` | Dedup | Replay: inventory inbox dedups; notifications may run again (no inbox) |
| `efcore-internal-command` | EF Core | `ReserveStockInternal` activity without Rabbit for that channel |
| `fanout-two-handlers-on-orderplaced` | Rabbit | Both notification handlers recorded |
| `oversized-payload-rolls-back` | Outbox | `POST /api/orders/oversized` rolls back order row |

Static assets live under `examples/Dashboard/wwwroot/` (`index.html`, `css/`, `js/`).

## Playground HTTP APIs (dev-only)

| Service | Endpoints |
|---|---|
| OrderService | `GET /api/playground/activities?orderId=`, `GET /api/playground/control-state`, `POST /api/playground/toggle`, `POST /api/orders/oversized` |
| InventoryService | same playground paths |
| NotificationService | same playground paths |
| Dashboard | `GET /api/playground/rabbit-depths` (queue main / retry / DLQ counts) |

### Toggle body

- Cycle (backward compatible): `{ "key": "<toggle-key>" }`.
- Explicit: `{ "key": "<toggle-key>", "mode": "succeed" \| "fail" \| "succeed-after", "failureCount": <n> }` (see each service’s `control-state` for keys).

## Manual demo sequences

### Inventory inbox poison (throw)

1. Dashboard: under **InventoryService**, set **Consume ProcessOrderCommand** to **throw** (or cycle until throw).
2. **Place Order (Outbox)** or **Place Order (Direct)**.
3. Wait for the InventoryService inbox poisoned panel; set mode to **off** and **Requeue** the poisoned row.

### Business rejection (reject)

1. Set inventory command mode to **reject**.
2. Place an order. Inventory stages `OrderFailed` via outbox; OrderService inbox marks the order **Failed**.

### Rabbit retry + DLQ (notifications)

1. Under **NotificationService**, set **Consume OrderPlaced** (notify or analytics) to **fail**.
2. Place an order. Watch **RabbitMQ queue depth** (retry then DLQ). Without inbox, transport retry applies to the **whole** delivery (both fan-out handlers are retried together).

### OrderService inbox handler failures

1. Toggle **Consume OrderFulfilled** or **Consume OrderFailed** to **fail** on OrderService.
2. Drive an order to fulfilled or failed, then observe poisoned OrderService inbox and **Requeue** after setting the toggle back to **succeed**.

## Project Structure

```
examples/
  AppHost/              Aspire host — services + infra
  ServiceDefaults/      Shared OTEL + health checks
  PlaygroundMessages/   Contracts, handler keys, ids, shared playground types
  OrderService/         Outbox + inbox + EF internal channel + management API + flow API + playground APIs
  InventoryService/     Command consume, outbox for outcomes, inbox, playground APIs
  NotificationService/  Fan-out consumer, Rabbit retry, playground APIs
  Dashboard/            Playground UI (scenario runner + timeline + toggles) + Rabbit depth endpoint
```

## Design notes

- **InventoryService stages `OrderFulfilled` / `OrderFailed` in its outbox** in the same `SaveChanges` as inbox-side effects, so the happy path does not rely on `PublishDirectAsync` from the handler.
- **NotificationService has no inbox.** Use **parameterless** `.WithHandler<THandler>()` so handlers are fire-and-forget; stable keys require `UseInbox` on the channel. Same `OrderPlaced` id replayed from the Dashboard fires handlers again; inventory command replay is deduplicated by inbox when ids match.
- **Requeue on Inventory poisoned rows** is disabled while inventory mode is **throw**, so you do not immediately re-poison during a live demo.

## Database shape changes

If you use persistent Postgres volumes from an older checkout, reset the dev volume after pulling changes that alter `Order` or `InventoryDbContext` layout (`EnsureCreated` only applies cleanly on empty databases). Recent columns include `Orders.PublishOrigin` and `StatusChangedAt` on orders.
