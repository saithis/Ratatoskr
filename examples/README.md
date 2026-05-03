# Ratatoskr E-Commerce Playground

A multi-service example that demonstrates major Ratatoskr features in one runnable demo.

## The Scenario

A simplified order pipeline across three services:

| Service | Role |
|---|---|
| **OrderService** | Creates orders, stages `OrderPlaced` and `ProcessOrderCommand` in one outbox transaction (or publishes both direct after persisting the order), consumes `OrderFulfilled` / `OrderFailed` via inbox |
| **InventoryService** | Consumes `ProcessOrderCommand` with inbox deduplication; stages `OrderFulfilled` / `OrderFailed` through its **outbox**; demo modes throw (inbox retries), reject (`OrderFailed`), or succeed (`OrderFulfilled`) |
| **NotificationService** | Fan-out consumer for `OrderPlaced` / `OrderFulfilled` without inbox; per-handler toggles drive **RabbitMQ** retry and DLQ |
| **Dashboard** | Single-page app: order flow, merged in-memory activity timeline, per-service playground toggles, Rabbit queue depth (main / `.retry` / `.dlq`), and management APIs for poisoned outbox/inbox |

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
| Outbox (EF Core, two messages one transaction) | `OrderService` — `POST /api/orders` stages `OrderPlaced` + `ProcessOrderCommand` with stable CloudEvents ids (`PlaygroundMessageIds`) |
| Direct publish (no outbox) | `OrderService` — `POST /api/orders/direct` persists the order row then `PublishDirectAsync` for both messages (same flow API as outbox) |
| Replay (dedup demo) | `OrderService` — `POST /api/orders/{id}/replay` republishes with the same ids |
| `[RatatoskrMessage]` attribute | All message types in `PlaygroundMessages` |
| Event publish/consume | `OrderPlaced`, `OrderFulfilled`, `OrderFailed` |
| Command send/consume | `ProcessOrderCommand` |
| Fan-out (topic exchange) | `OrderPlaced` to NotificationService + routing to inventory pipeline |
| **Inbox** retries + poison | Inventory **throw** mode: handler throws; inbox processor retries until poisoned |
| **Rabbit** retry + DLQ | Consumer queues use managed retry (`WithRetry(3, 5s)` on notifications, inventory commands, and OrderService event inbox); DLQ `*.dlq` |
| **Direct consume** (no inbox) | `NotificationService` handlers run on the consumer thread |
| Inbox deduplication | InventoryService command inbox; replay uses stable message ids |
| Management API + requeue | OrderService + InventoryService poisoned outbox/inbox |
| Outbox relay polling | OrderService + InventoryService: 2 s |
| Inbox polling | OrderService + InventoryService: 2 s for demo responsiveness |
| Inbox retention/cleanup | InventoryService: 30 min retention, 5 min cleanup |
| RabbitMQ transport | All services |
| EF Core durability | OrderService (`ordersdb`), InventoryService (`inventorydb`) |
| Playground observability | Each service registers `PlaygroundActivityRecorder` (`IMessageActivityObserver`) — `GET /api/playground/activities?orderId=` |

## Playground HTTP APIs (dev-only)

| Service | Endpoints |
|---|---|
| OrderService | `GET /api/playground/activities?orderId=`, `GET /api/playground/control-state`, `POST /api/playground/toggle` |
| InventoryService | same paths |
| NotificationService | same paths |
| Dashboard | `GET /api/playground/rabbit-depths` (queue main / retry / DLQ counts) |

Toggle bodies use `{ "key": "<toggle-key>" }` as returned by `control-state`.

## Demo sequences

### Inventory inbox poison (throw)

1. Dashboard: under **InventoryService**, toggle **Consume ProcessOrderCommand** until mode is **throw**.
2. **Place Order (Outbox)** or **Place Order (Direct)**.
3. Wait for the InventoryService inbox poisoned panel; then set mode to **off** and **Requeue** the poisoned row.

### Business rejection (reject)

1. Toggle inventory command mode to **reject**.
2. Place an order. Inventory stages `OrderFailed` via outbox; OrderService inbox marks the order **Failed**.

### Rabbit retry + DLQ (notifications)

1. Under **NotificationService**, toggle **Consume OrderPlaced** (or **OrderFulfilled**) to **fail**.
2. Place an order. Watch **RabbitMQ queue depth** on the dashboard (retry then DLQ). Handler runs without inbox, so transport retry applies.

### OrderService inbox handler failures

1. Toggle **Consume OrderFulfilled** or **Consume OrderFailed** to **fail** on OrderService.
2. Drive an order to fulfilled or failed, then observe poisoned OrderService inbox and **Requeue** after turning the toggle back to **succeed**.

## Project Structure

```
examples/
  AppHost/              Aspire host — services + infra
  ServiceDefaults/      Shared OTEL + health checks
  PlaygroundMessages/   Contracts, handler keys, ids, shared playground types
  OrderService/         Outbox + inbox + management API + flow API + playground APIs
  InventoryService/     Command consume, outbox for outcomes, inbox, playground APIs
  NotificationService/  Fan-out consumer, Rabbit retry, playground APIs
  Dashboard/            Playground UI + Rabbit depth endpoint
```

## Design notes

- **InventoryService stages `OrderFulfilled` / `OrderFailed` in its outbox** in the same `SaveChanges` as inbox-side effects, so the happy path does not rely on `PublishDirectAsync` from the handler.
- **NotificationService has no inbox.** Same `OrderPlaced` id replayed from the Dashboard fires the handler again; inventory command replay is deduplicated by inbox when ids match.
- **Requeue on Inventory poisoned rows** is disabled while inventory mode is **throw**, so you do not immediately re-poison during a live demo.

## Database shape changes

If you use persistent Postgres volumes from an older checkout, reset the dev volume after pulling changes that alter `Order` or `InventoryDbContext` layout (`EnsureCreated` only applies cleanly on empty databases). Recent columns include `Orders.PublishOrigin` and `StatusChangedAt` on orders.
