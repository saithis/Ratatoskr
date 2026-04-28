---
date: 2026-04-25
topic: expanded-examples
---

# Expanded Examples: Multi-Service E-Commerce Playground

## What We're Building

Replace the single-service PlaygroundApi with a realistic e-commerce scenario spanning three services plus a consolidated dashboard. The goal is to make every significant Ratatoskr feature observable and triggerable from one place, so manual testing and demos are fast and self-explanatory.

The three services model a simplified order pipeline: **OrderService** creates orders and starts message flows, **InventoryService** processes them (and can be made to fail), and **NotificationService** reacts to events independently. A separate **Dashboard** app provides a single HTML page to trigger flows and observe the state of all services.

## Services

### OrderService
- Publishes `OrderPlaced` event via the **outbox** (EF Core + RabbitMQ topic exchange)
- Publishes `OrderPlacedDirect` via **direct publish** (no outbox, fire-and-forget)
- Sends `ProcessOrderCommand` **command** to InventoryService (direct exchange)
- Sends one "custom" message using `MessageProperties` at call time instead of a `[RatatoskrMessage]` attribute
- Consumes `OrderFulfilled` and `OrderFailed` events from InventoryService
- Persists orders in PostgreSQL (EF Core)

### InventoryService
- Consumes `ProcessOrderCommand` via **command channel**
- Publishes `OrderFulfilled` or `OrderFailed` events
- Has a configurable failure mode (toggle via API) to drive **retry and dead-lettering**
- Inbox table for **deduplication** (same message ID processed only once)
- PostgreSQL + EF Core

### NotificationService
- Consumes `OrderPlaced` event -- demonstrates **multi-service fan-out** (same event consumed independently by both InventoryService and NotificationService)
- Consumes `OrderFulfilled` event
- In-memory only (no persistence needed), lightweight

### Dashboard (separate project)
- Single-page HTML + vanilla JS app hosted by a thin ASP.NET Core app
- **Trigger section per service**: buttons for Place Order (outbox), Place Order (direct), Send Duplicate (inbox dedup demo), Toggle Failure Mode, Requeue Failed Messages
- **State section per service**: polls each service's Ratatoskr management API every 3 seconds -- shows outbox queue depth, inbox processed count, failed/poisoned messages
- Service URLs configured via environment variables (Aspire injects them)

## Feature Coverage Matrix

| Feature | Service |
|---|---|
| Outbox (EF Core) | OrderService |
| Direct publish | OrderService |
| Configured message (`[RatatoskrMessage]`) | OrderPlaced, ProcessOrderCommand |
| Custom/unconfigured message (MessageProperties at call site) | OrderService custom send |
| Event publish + consume | OrderPlaced, OrderFulfilled, OrderFailed |
| Command send + consume | ProcessOrderCommand |
| Multi-service fan-out | OrderPlaced → InventoryService + NotificationService |
| Retry + dead-lettering | InventoryService failure mode |
| Inbox deduplication | InventoryService (Dashboard sends duplicate message IDs) |
| Management API + requeue | All services, surfaced in Dashboard |
| RabbitMQ transport | All services |
| EF Core transport | OrderService, InventoryService |

## Key Decisions

- **Separate Dashboard project**: keeps the UI decoupled from any service; Dashboard has no domain logic
- **Vanilla JS dashboard**: no framework overhead for what is essentially a dev-tools page; easy to understand and modify
- **Failure mode toggle API**: a simple `POST /api/inventory/failure-mode` endpoint lets the dashboard enable/disable failures without restarting the service
- **Duplicate endpoint**: `POST /api/orders/{id}/replay` re-publishes an existing OrderPlaced event with the same message ID, triggering the inbox deduplication check in InventoryService
- **All services in AppHost**: Aspire orchestrates all four projects, PostgreSQL, and RabbitMQ -- single `dotnet run` in AppHost starts everything
- **Shared Messages project**: a `PlaygroundMessages` class library holds all message types shared across services; avoids duplication and makes the contracts visible in one place

## Project Structure

```
examples/
  AppHost/                  (existing, extended with new services)
  PlaygroundMessages/       (new -- shared message contracts)
  OrderService/             (replaces PlaygroundApi)
  InventoryService/         (new)
  NotificationService/      (new)
  Dashboard/                (new -- HTML dashboard)
```

## Resolved Questions

- **Separate databases per service**: each service gets its own PostgreSQL database in Aspire, reflecting realistic microservices boundaries.
- **Auto-refresh every 3 seconds**: the Dashboard polls all service management APIs automatically so flows are visible in real time without manual interaction.
