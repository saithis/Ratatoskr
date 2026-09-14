# Ratatoskr examples playground

Two ASP.NET Core hosts (**PlaygroundHost** and **InventoryService**) orchestrated by an Aspire **AppHost** demonstrate Ratatoskr building blocks: EF Core outbox/inbox on PostgreSQL, RabbitMQ fan-out and retries, a **playground** scenario runner, and the **Ratatoskr Management Dashboard** (`Ratatoskr.UI` + `Ratatoskr.Management`).

## Topology

| Piece | Role |
|---|---|
| **PlaygroundHost** | Scenario runner, activity recorder, playground UI at `/`, and the **Ratatoskr Management Dashboard** at `/ratatoskr` |
| **InventoryService** | Companion microservice managed purely over RabbitMQ (`Ratatoskr.Management`), hosting two DbContexts (`InventoryDbContext` and `AuditDbContext`) |
| **publisherdb** | Publisher `DbContext` on PostgreSQL: orders table, outbox, inbox, EF Core internal channel |
| **consumerdb** | Consumer `DbContext` on PostgreSQL: command inbox + outcome outbox |
| **playgrounddb** | Scenario run ledger (`Runs` table) |
| **inventorydb** | Inventory `DbContext`: outbox **and** inbox for stock reservations |
| **auditdb** | Audit `DbContext`: outbox **only**, demonstrating asymmetric multi-DbContext configurations in the Management UI |
| **RabbitMQ** | Message broker for scenario channels, plus the Management UI 2-exchange control plane (`ratatoskr.ui.commands` and `ratatoskr.ui.inbox`) |

## Quick start

```bash
cd examples/AppHost
aspire run
# or: dotnet run
```

1. Open the Aspire dashboard (typically `http://localhost:15000`) to inspect resource status, console logs, and distributed traces.
2. Open the **playgroundhost** HTTP endpoint (`http://localhost:<port>/`) to launch scenarios.
3. Open the **Ratatoskr Management Dashboard** at **`/ratatoskr`** (`http://localhost:<port>/ratatoskr`) to see the new management UI in action.

---

## Demonstrating Management UI Capabilities

The new **`Ratatoskr.UI`** and **`Ratatoskr.Management`** packages provide a distributed control plane operating over RabbitMQ:

### 1. Multi-Service Automatic Discovery
- Both `playground-host` and `inventory-service` periodically publish health heartbeats to `ratatoskr.ui.inbox`.
- The dashboard automatically discovers connected microservices in real-time without needing hardcoded HTTP addresses or direct inter-service HTTP ingress.
- Real-time updates stream directly to the browser via **Server-Sent Events (SSE)** (`/ratatoskr/api/events`).

### 2. Multi-DbContext per Service
- Switch between services in the sidebar or header:
  - **`playground-host`**: Features `PublisherDbContext` (Outbox + Inbox) and `ConsumerDbContext` (Outbox + Inbox).
  - **`inventory-service`**: Features `InventoryDbContext` (Outbox + Inbox) and `AuditDbContext` (Outbox only). The UI clearly displays which durability patterns are active for each context.

### 3. Active Replicas & Instance Tracking
- Under **Replicas & Stats**, inspect active instance IDs, host/machine names, environment names, start time, and last heartbeat timestamps.

### 4. Channels & Topology Visualization
- Under **Channels & Topology**, inspect registered publish and consume channels, intents (`CommandConsume`, `EventPublish`, etc.), transport details (`rabbitmq` / `efcore`), queue/exchange names, and accepted message types.

### 5. Outbox & Inbox Poison Inspection & Remediation
- **Generate Outbox Poison** (`PlaygroundHost`):
  - Run the `outbox-poison` scenario from `/`.
  - Open `/ratatoskr`, select `playground-host` -> `PublisherDbContext` -> Outbox.
  - Inspect the poisoned message, view CloudEvents headers and decoded JSON payload, and click **Requeue** or **Bulk Requeue**.
- **Generate Inbox Poison** (`PlaygroundHost`):
  - Run the `inbox-poison` scenario from `/`.
  - Open `/ratatoskr`, select `playground-host` -> `ConsumerDbContext` -> Inbox.
  - View the failing handler (`inbox-poison.process`), attempt count, and full exception stack trace.
  - Test **Requeue Handler**, **Requeue Message**, or **Bulk Requeue**.
- **Generate Multi-Service Inbox Poison** (`InventoryService`):
  - Trigger a failing stock reservation on `InventoryService`:
    ```bash
    curl -X POST http://localhost:<inventory-port>/inventory/reservations/simulate-failure
    ```
  - Open `/ratatoskr`, select `inventory-service` -> `InventoryDbContext` -> Inbox.
  - Notice the row is poisoned in `InventoryDbContext` with the simulated error stack trace.
  - Click **Requeue** — the management UI dispatches the command over RabbitMQ to `inventory-service.mgmt`, where the agent unpoisons the message and clears the error counter.

---

## Feature coverage (where)

| Feature | Where |
|---|---|
| Outbox (publisher stages command; consumer returns outcome event) | Scenarios `outbox-success`, `outbox-retry-then-success` |
| Outbox (multi-message one transaction) | Scenario `efcore-internal-command` (two internal rows in one `SaveChanges`) |
| Outbox transport failure until poison | Scenario `outbox-retry-then-success`, `outbox-poison` (run-scoped outbox send registry) |
| Outbox max message size | `WithMaxMessageSize` on publisher outbox; scenario `oversized-payload-rolls-back` |
| EF Core internal channel | Scenario `efcore-internal-command` |
| Direct publish / consume | Scenarios `direct-consume-success`, `direct-consume-retry`, `direct-consume-dlq` |
| Replay deduplication | Scenario `replay-dedups` |
| Fan-out (two handlers, one queue) | Scenario `fanout-two-handlers-on-orderplaced` |
| Inbox retries / poison | Scenarios `inbox-poison`, `inbox-retry-then-success` |
| Business rejection path | Scenario `business-rejection` |
| Management Dashboard | Embedded SPA at `http://localhost:<playground-port>/ratatoskr` (`Ratatoskr.UI`) |
| Multi-service broker management | Real-time discovery & RPC over RabbitMQ between `PlaygroundHost` and `InventoryService` |
| Asymmetric multi-DbContexts | `InventoryService` (`InventoryDbContext` outbox+inbox vs `AuditDbContext` outbox only) |
| Activity log | `PlaygroundActivityRecorder` — `GET /api/playground/activities?orderId=` or `?scenarioRunId=` |

## Project layout

```
examples/
  AppHost/           Aspire — postgres (5 databases) + rabbitmq + PlaygroundHost + InventoryService
  PlaygroundHost/    Scenario runner host + wwwroot playground + Ratatoskr.UI dashboard at /ratatoskr
  InventoryService/  Dedicated microservice managed over RabbitMQ (Ratatoskr.Management)
  ServiceDefaults/   Shared OpenTelemetry + health check extensions
  Docs/              Docfx snippets only
```

## Tests

Integration test coverage for the examples and management dashboard lives in **`tests/Ratatoskr.Tests`**:
- `Examples/PlaygroundHostScenarioHttpTests.cs`: Playground scenario runner execution tests.
- `Examples/PlaygroundHostManagementUiTests.cs`: PlaygroundHost `/ratatoskr` UI endpoint and asset verification.
- `Examples/InventoryServiceManagementTests.cs`: InventoryService broker management agent, multi-DbContext, and poison requeue verification.
- `Integration/Management/`: Library-level RabbitMQ and In-Process management tests.
