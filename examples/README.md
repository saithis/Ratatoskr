# Ratatoskr E-Commerce Playground

A multi-service example that demonstrates major Ratatoskr features in one runnable demo.

## The Scenario

A simplified order pipeline across three services:

| Service | Role |
|---|---|
| **OrderService** | Creates orders, stages `OrderPlaced` and `ProcessOrderCommand` in one outbox transaction (or publishes both direct), consumes `OrderFulfilled` / `OrderFailed` via inbox |
| **InventoryService** | Consumes `ProcessOrderCommand` with inbox deduplication; demo modes throw (inbox retries), reject (`OrderFailed` event), or succeed (`OrderFulfilled`) |
| **NotificationService** | Fan-out consumer for `OrderPlaced` / `OrderFulfilled` without inbox; optional throw mode drives **RabbitMQ** retry and DLQ |
| **Dashboard** | Single-page app to trigger flows, poll order flow, and use management APIs for poisoned outbox/inbox |

## Quick Start

Use the [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) CLI (the Aspire workload is not required):

```bash
cd examples/AppHost
aspire run
# or: dotnet run
```

The Aspire dashboard opens (for example at http://localhost:15000). Open the **dashboard** resource URL to load the playground UI. Use the RabbitMQ resource (management plugin) to inspect queues, including `ecommerce.events.notifications.dlq` after notification failures.

## Feature Coverage

| Feature | Where |
|---|---|
| Outbox (EF Core, two messages one transaction) | `OrderService` — `POST /api/orders` stages `OrderPlaced` + `ProcessOrderCommand` with stable CloudEvents ids (`PlaygroundMessageIds`) |
| Direct publish (no outbox) | `OrderService` — `POST /api/orders/direct` publishes both messages |
| Replay (dedup demo) | `OrderService` — `POST /api/orders/{id}/replay` republishes with the same ids |
| `[RatatoskrMessage]` attribute | All message types in `PlaygroundMessages` |
| Event publish/consume | `OrderPlaced`, `OrderFulfilled`, `OrderFailed` |
| Command send/consume | `ProcessOrderCommand` |
| Fan-out (topic exchange) | `OrderPlaced` to NotificationService + routing to inventory pipeline |
| **Inbox** retries + poison | Inventory **throw** mode: handler throws; inbox processor retries (see `UseInbox` polling, 2 s in this sample) until poisoned |
| **Rabbit** retry + DLQ | Notification failure toggle: handler throws on consumer thread; queue `ecommerce.events.notifications` uses `WithRetry(3, 5s)` → DLQ `ecommerce.events.notifications.dlq` |
| Inbox deduplication | InventoryService command inbox; replay uses stable message ids |
| Management API + requeue | OrderService + InventoryService poisoned outbox/inbox |
| Outbox relay polling | OrderService: 2 s |
| Inbox polling | OrderService + InventoryService: 2 s for demo responsiveness |
| Inbox retention/cleanup | InventoryService: 30 min retention, 5 min cleanup |
| RabbitMQ transport | All services |
| EF Core durability | OrderService (`ordersdb`), InventoryService (`inventorydb`) |

## Demo sequences

### Inventory inbox poison (throw)

1. Dashboard: **Cycle inventory mode** until it shows **throw**.
2. **Place Order (Outbox)**.
3. Wait for the InventoryService inbox poisoned count (inbox polling is 2 s; retries use inbox backoff until poisoned).
4. Cycle mode to **off**, then **Requeue** on the poisoned row.

### Business rejection (reject)

1. Cycle inventory mode to **reject**.
2. **Place Order (Outbox)**. Order row moves to **Failed** after `OrderFailed` is processed on OrderService.

### Rabbit DLQ (notifications)

1. **Toggle notification failure** on.
2. **Place Order (Outbox)** (or direct). After Rabbit retries (3 × 5 s), inspect **ecommerce.events.notifications.dlq** in RabbitMQ Management.

## Project Structure

```
examples/
  AppHost/              Aspire host — services + infra
  ServiceDefaults/      Shared OTEL + health checks
  PlaygroundMessages/   Contracts, handler keys, PlaygroundMessageIds
  OrderService/         Outbox + inbox + management API + flow API
  InventoryService/     Command consume, tri-state demo mode, inbox
  NotificationService/  Fan-out consumer, Rabbit retry, failure toggle
  Dashboard/            Playground UI
```

## Design notes

- **InventoryService publishes `OrderFulfilled` / `OrderFailed` without an outbox.** Small loss window if the process crashes after publish and before inbox completion; handler comments describe the trade-off.
- **NotificationService has no inbox.** Same `OrderPlaced` id replayed from the Dashboard fires the handler again; inventory command replay is deduplicated by inbox when ids match.
- **Requeue on Inventory poisoned rows** is disabled while inventory mode is **throw**, so you do not immediately re-poison during a live demo.

## Database shape changes

If you use persistent Postgres volumes from an older checkout, add column `StatusChangedAt` on the orders table or reset the dev volume after pulling changes that alter `Order` entity layout (`EnsureCreated` only applies cleanly on empty databases).
