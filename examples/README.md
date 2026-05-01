# Ratatoskr E-Commerce Playground

A multi-service example that demonstrates every major Ratatoskr feature in one runnable demo.

## The Scenario

A simplified order pipeline across three services:

| Service | Role |
|---|---|
| **OrderService** | Creates orders, publishes `OrderPlaced` via outbox or direct publish, consumes `OrderFulfilled`/`OrderFailed` |
| **InventoryService** | Processes orders (can be made to fail), publishes results, inbox deduplication |
| **NotificationService** | Lightweight fan-out consumer, no inbox, logs only |
| **Dashboard** | Single-page app to trigger flows and observe outbox/inbox state |

## Quick Start

```bash
dotnet workload install aspire   # once, if not installed
cd examples/AppHost
dotnet run
```

The Aspire Dashboard opens at http://localhost:15000. Find the **dashboard** service URL in the dashboard to open the playground UI.

## Feature Coverage

| Feature | Where |
|---|---|
| Outbox (EF Core, transactional) | `OrderService` — `POST /api/orders` |
| Direct publish (no outbox) | `OrderService` — `POST /api/orders/direct` |
| `[RatatoskrMessage]` attribute | All message types in `PlaygroundMessages` |
| Event publish/consume | `OrderPlaced`, `OrderFulfilled`, `OrderFailed` |
| Command send/consume | `ProcessOrderCommand` |
| Fan-out (topic exchange) | `OrderPlaced` → InventoryService + NotificationService |
| Retry + dead-lettering | InventoryService failure mode (3 retries × 5 s) |
| Inbox deduplication | InventoryService + Dashboard "Replay" button |
| Management API + requeue | OrderService + InventoryService, shown in Dashboard |
| Outbox relay polling | 2 s override (default is 60 s) |
| Inbox retention/cleanup | InventoryService: 30 min retention, 5 min cleanup cycle |
| RabbitMQ transport | All services |
| EF Core transport | OrderService (`ordersdb`), InventoryService (`inventorydb`) |

## Demo Sequence: Failure Recovery

1. In the Dashboard, click **Toggle Failure Mode** — InventoryService will now throw on every `ProcessOrderCommand`.
2. Click **Place Order (Outbox)**.
3. Wait ~15 s for retries to exhaust (3 × 5 s). The poisoned count increments in the Dashboard.
4. Click **Toggle Failure Mode** again to disable it.
5. Click **Requeue** on the poisoned message. InventoryService processes the order successfully.

## Project Structure

```
examples/
  AppHost/              .NET Aspire host — starts all services + infra
  ServiceDefaults/      Shared OTEL + health check setup (Aspire pattern)
  PlaygroundMessages/   Shared message contracts and HandlerKeys constants
  OrderService/         Outbox, inbox, management API
  InventoryService/     Command consume, failure mode toggle, inbox dedup
  NotificationService/  Fan-out consumer, no persistence
  Dashboard/            Single-page HTML app
```

## Design Notes

- **InventoryService publishes `OrderFulfilled` without an outbox.** There is a small loss window if the service crashes between `PublishDirectAsync` and completing the inbox handler. The handler comment explains this trade-off.
- **NotificationService has no inbox.** Replaying an `OrderPlaced` with the same message ID triggers the notification handler again — intentional contrast with InventoryService which deduplicates.
- **Requeue while failure mode is ON** would re-poison the message. The Dashboard disables the Requeue button while failure mode is active.
