# Goal Description

Provide a Management UI and accompanying API for the Ratatoskr library to view poisoned messages from the Inbox and Outbox, and allow users to actively requeue or delete them. This UI also serves as a foundation for future features such as displaying system statuses, throughput, and other internal metrics.

The frontend is built using HTML, CSS, TypeScript, and **Angular 21**.

Critically, **the UI supports managing multiple backends** at a glance, acting as a global dashboard aggregating health data across all your microservices seamlessly.

---

## Proposed Changes

### 1. Ratatoskr Core — Extensibility Pattern

- Add `IRatatoskrEndpointConfigurator` interface to core (core already has `FrameworkReference Microsoft.AspNetCore.App`).
- A single extension method `app.MapRatatoskrManagementApi(policyName)` resolves all registered `IRatatoskrEndpointConfigurator` implementations from DI and registers their Minimal API endpoints.
- **Authorization is required**: the caller must provide a policy name. No open/anonymous mode.
- All endpoints are versioned under `/ratatoskr/api/v1/`.

### 2. Ratatoskr.EfCore — Management Endpoints

- Add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `Ratatoskr.EfCore.csproj`.
- Implement `IRatatoskrEndpointConfigurator` inside `Ratatoskr.EfCore`. Endpoints are registered there directly — no cross-project InternalsVisibleTo needed since EfCore already sees its own entity classes.
- Add `RequeuedCount` column to `OutboxMessageEntity` and `InboxHandlerStatusEntity` via EF model configuration only (no auto-migration; users with manual migrations apply it themselves).
- Endpoints expose fresh DB queries for poisoned lists and use cached `EfCoreMetricsState` gauges for dashboard counts, supplemented by targeted DB queries for data not in the cache (e.g., last processed timestamp).
- When a requeue occurs: clear `IsPoisoned`, `ErrorCount`, `Error`, `NextAttemptAt`; increment `RequeuedCount`. ErrorCount resets so the full retry cycle runs again.

**Outbox endpoints:**
- `GET /ratatoskr/api/v1/outbox/poisoned` — paginated list, cursor-based, filterable by message type and date range
- `GET /ratatoskr/api/v1/outbox/poisoned/{id}` — message detail with JSON payload + metadata
- `POST /ratatoskr/api/v1/outbox/poisoned/{id}/requeue` — requeue one message
- `DELETE /ratatoskr/api/v1/outbox/poisoned/{id}` — delete one message
- `POST /ratatoskr/api/v1/outbox/poisoned/requeue` — bulk requeue (body: list of IDs, or `{ all: true }`)
- `DELETE /ratatoskr/api/v1/outbox/poisoned` — bulk delete (body: list of IDs, or `{ all: true }`)
- `GET /ratatoskr/api/v1/health` — health overview (poisoned count, pending backlog count, last processed timestamp, processing rate from cache)

**Inbox endpoints:**
- `GET /ratatoskr/api/v1/inbox/poisoned` — paginated list of poisoned `InboxHandlerStatusEntity` rows, with parent message metadata; filterable by message type and date range
- `GET /ratatoskr/api/v1/inbox/poisoned/{handlerStatusId}` — handler status detail with JSON payload + metadata panel
- `GET /ratatoskr/api/v1/inbox/messages/{messageId}/handlers` — all handler statuses for a given message
- `POST /ratatoskr/api/v1/inbox/poisoned/{handlerStatusId}/requeue` — requeue one handler status
- `POST /ratatoskr/api/v1/inbox/messages/{messageId}/requeue` — requeue all poisoned handlers for a message
- `DELETE /ratatoskr/api/v1/inbox/poisoned/{handlerStatusId}` — delete one handler status
- `POST /ratatoskr/api/v1/inbox/poisoned/requeue` — bulk requeue (body: list of handler status IDs, or `{ all: true }`)
- `DELETE /ratatoskr/api/v1/inbox/poisoned` — bulk delete

> **Cleanup note:** Both `OutboxCleanupService` and `InboxCleanupService` already filter `!x.IsPoisoned`, so poisoned messages are never auto-deleted. No change needed.

> **Multi-DbContext note:** A service may have multiple DbContexts registered with Ratatoskr. All are aggregated into a flat response with a `dbContext` metadata field identifying the source.

### 3. Ratatoskr.RabbitMq — Health Endpoint

- Implement `IRatatoskrEndpointConfigurator` inside `Ratatoskr.RabbitMq`.
- Register a single health endpoint: `GET /ratatoskr/api/v1/rabbitmq/health` — returns connection status and channel health.
- No poisoned message management (RabbitMq has no persistent poisoned message store).

### 4. Ratatoskr.UI — Dashboard Host + Proxy (new NuGet package)

- A standalone class library + NuGet package.
- Integrated via `app.UseRatatoskrUi(options)`.
- Base path defaults to `/ratatoskr/` with configurable override via `options.BasePath`.

**Backend configuration:**

```csharp
app.UseRatatoskrUi(options =>
{
    // Service hosting the UI — routes in-process, no HTTP round-trip, no URL needed
    options.AddLocalBackend("Orders");

    options.AddBackend("Inventory", "https://inventory-api", auth =>
        auth.ForwardCookies()); // built-in helper wrapping the delegate

    options.AddBackend("Payments", "https://payments-api", auth =>
        auth.UseDelegate(async req =>
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokenProvider.GetTokenAsync());
        }));
});
```

Auth is configured via an **extensible delegate** (`Func<HttpRequestMessage, Task>`) per backend, with convenience helpers (e.g., `ForwardCookies()`, `UseApiKey(key)`) shipped as built-in wrappers.

**Local backend (`AddLocalBackend`):**

When the service hosting the UI also runs its own management API, `AddLocalBackend(name)` registers it as a backend with no HTTP round-trip and no authentication requirement. The proxy middleware dispatches requests directly through the app's `RequestDelegate` using a synthetic `HttpContext` constructed via `IHttpContextFactory`. A custom request feature (`ILocalRatatoskrRequestFeature`) is set on that context; the authorization handler installed by `MapRatatoskrManagementApi` recognizes this feature and grants access unconditionally. Because this feature can only be set by in-process code — not via an HTTP header — it cannot be spoofed by external callers. From the Angular frontend's perspective, a local backend is indistinguishable from a remote one — it still routes through `/ratatoskr/api/v1/backends/{name}/*`.

**Proxy routing:**
- `GET /ratatoskr/api/v1/backends` — returns list of configured backends with name + status
- `GET /ratatoskr/api/v1/dashboard` — fan-out to all backends' `/health` endpoints in parallel (`Task.WhenAll`), aggregates results; failed backends return partial results with an error indicator
- `ANY /ratatoskr/api/v1/backends/{name}/*` — generic passthrough to the configured backend URL; transparent reverse proxy with no result transformation

All calls from the Angular app for single-service detail views go through this passthrough route.

**Error handling:** If a backend is unreachable, the dashboard response still returns data from healthy backends plus an `errors` array indicating which backends failed. The UI shows a warning banner for degraded services.

**Angular SPA hosting:** Built Angular output is served via `.NET 9+ MapStaticAssets()` for automatic compression, fingerprinting, and optimal caching. Assets are included in the NuGet package.

### 5. Angular 21 Frontend

Located in `src/Ratatoskr.UI/ClientApp`.

**Dev config:** Angular CLI proxy (`proxy.conf.json`) forwards `/ratatoskr/api/*` to the ASP.NET backend during dev. Production uses relative URLs.

**Navigation structure:**

```
Dashboard (global grid — all services at a glance)
  └─ Click service → Service Detail Page
       ├─ Tab: Overview  (health stats for this service)
       ├─ Tab: Outbox    (poisoned outbox messages for this service)
       └─ Tab: Inbox     (poisoned inbox messages for this service)
```

- **Dashboard page:** Grid of service cards, each showing: poisoned outbox count, poisoned inbox count, pending backlog, processing rates, last processed timestamp, RabbitMq connection status (if applicable). Colored indicators (green/red/yellow) per service. Data auto-refreshed at a configurable polling interval with a visible countdown.
- **Service detail — Overview tab:** Combined health stats for the service.
- **Service detail — Outbox tab:** Paginated list of poisoned outbox messages with columns: ID, message type, created at, error count, requeue count, last error snippet. Click row → message detail drawer with two-panel view: top = MessageProperties metadata, bottom = formatted JSON payload (fallback to base64 if not JSON). Filters: message type, date range. Bulk select + requeue / bulk select + delete.
- **Service detail — Inbox tab:** Paginated list of poisoned `InboxHandlerStatusEntity` rows with columns: message type, handler key, received at, error count, requeue count, last error. Expandable rows to show all handler statuses for the same parent message. Requeue targets single handler or all handlers on a message. Bulk select + requeue / delete.

**Theming:** Light/dark toggle in the UI header, preference stored in `localStorage`.

**No badges or toast notifications.** Dashboard grid colored indicators are sufficient.

---

## Verification Plan

- TUnit integration tests for all management Minimal API endpoints (outbox + inbox poisoned CRUD, requeue, bulk actions, health).
- Verify `RequeuedCount` increments correctly and `ErrorCount` resets on requeue.
- Verify that cleanup services skip poisoned messages (existing behavior; regression test).
- Verify MSBuild correctly compiles the Angular app inside the package.
- Verify Angular dev server works with `aspire run` and the `proxy.conf.json` correctly routes API calls.
- Add a full **OrderService** to `examples/AppHost` — a real API with its own `DbContext`, RabbitMq channels, and handlers, sharing the existing Postgres instance on a separate `ordersdb` database. Proves the UI aggregates multiple backends and the dashboard grid correctly shows two services.

---

## Decision Log

| Topic | Decision |
|---|---|
| Inbox granularity | Both views: message list (expandable) + per-handler requeue |
| Pagination across backends | Not needed — service-centric views; dashboard only shows counts |
| Proxy auth | Extensible delegate per backend with built-in helpers |
| RabbitMq endpoints | Health/status only |
| Payload display | JSON + metadata panel (no export in v1) |
| Management API auth | Required policy name — no anonymous mode |
| Multi-DbContext | Aggregate per service, `dbContext` field in response |
| Data refresh | Configurable polling interval with countdown indicator |
| Actions | Requeue + delete + bulk (both) |
| SPA asset hosting | MapStaticAssets (.NET 9+) |
| Dashboard overview | Full health (poisoned, backlog, processing rate, last processed) |
| Base path | `/ratatoskr/` default, configurable override |
| Requeue tracking | `RequeuedCount` column on entities; EF model only, no auto-migration |
| Proxy for detail views | Generic passthrough (`/backends/{name}/*`) |
| Local backend | `AddLocalBackend(name)` — in-process dispatch via synthetic `HttpContext` + `ILocalRatatoskrRequestFeature`; auth bypassed only for in-process requests (feature flag not settable via HTTP, so unspoofable); no self-URL, no credentials needed |
| Theming | Manual light/dark toggle, `localStorage` |
| Degraded backends | Partial results + error indicator in dashboard |
| OrderService example | Full second service, shared Postgres, separate `ordersdb` |
| Angular version | 21 |
| Dev API config | `proxy.conf.json` in Angular CLI |
| API versioning | `/ratatoskr/api/v1/` from day one |
| EfCore ASP.NET ref | Add `FrameworkReference Microsoft.AspNetCore.App` to EfCore |
| Internal entity access | Endpoints live in EfCore — no cross-project access needed |
| Dashboard metrics source | Hybrid: cached `EfCoreMetricsState` + targeted DB queries |
| Notifications | None — dashboard grid indicators sufficient |
| Cleanup + poisoned | Already excluded — no change needed |
| Packaging | `Ratatoskr.UI` as separate NuGet package |
