# Management API & Dashboard

Ratatoskr ships an HTTP management API (`Ratatoskr` + `Ratatoskr.EfCore`) and an embedded web
dashboard (`Ratatoskr.UI`) on top of it. The dashboard inspects channel topology, live process
metrics, and the poisoned outbox/inbox rows of every registered `DbContext` — across as many
services as you register.

## Setup

The dashboard is a client of the management API. Map both:

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("RatatoskrAdmin", p => p.RequireRole("ops")));

builder.Services.AddRatatoskrUI(ui =>
{
    ui.Title = "Orders Dashboard";
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// The API. A policy is required; it is validated at startup.
app.MapRatatoskrManagementApi("RatatoskrAdmin");

// The UI. Serves the SPA plus its own config endpoint under the route prefix.
app.MapRatatoskrUI("/ratatoskr", "RatatoskrAdmin");
```

`MapRatatoskrManagementApi` is a no-op when no transport registered management endpoints, so it
is safe to call from a host that only conditionally configures Ratatoskr durability.

If you move the API off its default base path, tell the dashboard where it went:

```csharp
builder.Services.AddRatatoskrUI(ui => ui.LocalManagementApiPath = "/admin/ratatoskr");
app.MapRatatoskrManagementApi("RatatoskrAdmin", "/admin/ratatoskr");
```

### Options

| Option | Default | Purpose |
|---|---|---|
| `Title` | `"Ratatoskr Dashboard"` | Header text. |
| `RoutePrefix` | `"/ratatoskr"` | Where the dashboard is served. The `routePrefix` argument of `MapRatatoskrUI` overrides it. |
| `LocalManagementApiPath` | `"/ratatoskr/api/v1"` | Where this host mounts its own management API. Must match the `basePath` given to `MapRatatoskrManagementApi`. |
| `PollingIntervalMs` | `5000` | Auto-refresh interval of the active tab. |
| `EnablePayloadEditing` | `true` | Allows editing an outbox payload before requeueing it. |
| `LocalServiceName` | `"This Host"` | Name of the hosting service in the service picker. |
| `IncludeLocalService` | `true` | Set to `false` for a dashboard host that only aggregates remote services. |
| `AddService(...)` | — | Registers a remote service, see [Multiple services](#multiple-services). |

## Multiple DbContexts

A service can register durability for several `DbContext` types, one per bounded context:

```csharp
builder.Services.AddRatatoskr(bus =>
{
    bus.AddEfCoreDurability<OrderDbContext>(d => d.UseOutbox().UseInbox());

    // Outbox only: this context stages messages but consumes nothing.
    bus.AddEfCoreDurability<AuditDbContext>(d => d.UseOutbox());
});
```

The management API keys every EF Core endpoint on the **short type name** of the `DbContext`:

```
GET /ratatoskr/api/v1/efcore/contexts
GET /ratatoskr/api/v1/efcore/contexts/OrderDbContext/health
GET /ratatoskr/api/v1/efcore/contexts/OrderDbContext/outbox/poisoned
GET /ratatoskr/api/v1/efcore/contexts/AuditDbContext/outbox/poisoned
```

Short names must therefore be unique. Two `DbContext` types with the same short name in different
namespaces would produce ambiguous URLs, so Ratatoskr throws at startup with both full CLR names
instead of silently answering for whichever won.

The dashboard's Poison Workbench shows one card per registered context with its poisoned and
pending backlog on each half, and switches the table between them. `GET /efcore/contexts` reports
which halves a context actually configured:

```json
{
  "contexts": [
    { "name": "AuditDbContext", "hasOutbox": true,  "hasInbox": false },
    { "name": "OrderDbContext", "hasOutbox": true,  "hasInbox": true  }
  ]
}
```

`hasOutbox`/`hasInbox` reflect the `UseOutbox()`/`UseInbox()` calls, not the interfaces the type
implements — `AddEfCoreDurability<T>` requires both interfaces regardless. Endpoints for a half
that was not configured answer `404`, and the dashboard disables the matching toggle.

> [!NOTE]
> The backlog counts on the context cards come from the EF Core gauge poller, which runs every 30
> seconds by default (`WithMetricsPollingInterval` on the durability builder), so they can trail
> the poison table. The table itself queries the database on every refresh.

## Multiple services

One dashboard can inspect several Ratatoskr services. Each remote service only needs to map the
management API — it does not have to host a UI of its own.

**On each remote service:**

```csharp
app.MapRatatoskrManagementApi("RatatoskrAdmin");
```

**On the dashboard host:**

```csharp
builder.Services.AddRatatoskrUI(ui =>
{
    ui.LocalServiceName = "Orders";
    ui.AddService("Inventory", "https://inventory.internal");
    ui.AddService("Shipping", new Uri("https://shipping.internal"));
});
```

`AddService` takes the **service root** and appends the default management API path
(`/ratatoskr/api/v1`). If a service mounts the API somewhere else, pass that path explicitly:

```csharp
// MapRatatoskrManagementApi("RatatoskrAdmin", "/admin/ratatoskr") on the remote service
ui.AddService("Legacy", "https://legacy.internal", "/admin/ratatoskr");
```

Service names are unique, must not contain `/`, and double as the key the dashboard addresses a
service by. Registering the same name twice throws at startup.

### How requests reach a remote service

The browser never calls a remote service directly — that would need CORS on every service and
would expose internal URLs to the client. Instead the dashboard host relays:

```
browser → GET /ratatoskr/ui-api/proxy/Inventory/efcore/contexts
        → https://inventory.internal/ratatoskr/api/v1/efcore/contexts
```

The relay forwards the method, query string, request body, and `Authorization` header, and passes
the remote status code and body straight back. It is only mapped when at least one remote service
is registered, so a single-service host exposes no outbound request surface at all.

Because the relay is a plain named `HttpClient` (`"RatatoskrUIProxy"`), you can configure it like
any other — including .NET Aspire service discovery, which lets you register a service by its
logical name:

```csharp
// With ServiceDefaults' ConfigureHttpClientDefaults(http => http.AddServiceDiscovery())
ui.AddService("Inventory", "https+http://inventoryservice");
```

To harden or instrument the relay, configure the named client directly:

```csharp
builder.Services
    .AddHttpClient("RatatoskrUIProxy")
    .AddStandardResilienceHandler();
```

### A dedicated dashboard host

A host that runs no Ratatoskr durability of its own can aggregate only remote services:

```csharp
builder.Services.AddRatatoskrUI(ui =>
{
    ui.IncludeLocalService = false;
    ui.AddService("Orders", "https://orders.internal");
    ui.AddService("Inventory", "https://inventory.internal");
});
```

## Behind a reverse proxy

The dashboard roots every asset and API URL at `HttpContext.Request.PathBase`, so mounting the
host under a sub-path works without configuration as long as the proxy sets it:

```csharp
app.UsePathBase("/tools");   // dashboard is served at /tools/ratatoskr
```

## Security

The management API can requeue and delete messages, and the topology endpoint exposes handler and
message type names. Treat it as an administrative surface:

- `MapRatatoskrManagementApi` **requires** a policy and validates it at startup.
- `MapRatatoskrUI` takes an optional policy. Pass one whenever the dashboard is reachable from
  outside the cluster.
- The relay forwards the caller's `Authorization` header, so a bearer token that is valid on the
  dashboard host must also be accepted by the remote services.
- Set `EnablePayloadEditing = false` to stop operators from rewriting a payload before requeueing.

## Endpoint reference

All paths are relative to the management API base path (`/ratatoskr/api/v1` by default).

| Method | Path | Purpose |
|---|---|---|
| GET | `/system/topology` | Registered channels, message types, and handlers |
| GET | `/system/metrics` | Instance id, environment, uptime, memory, channel counts |
| GET | `/efcore/contexts` | Registered DbContexts and their configured halves |
| GET | `/efcore/contexts/{context}/health` | Poisoned/pending gauges and last processing timestamps |
| GET | `/efcore/contexts/{context}/outbox/poisoned` | Poisoned outbox rows (paged, filterable) |
| GET | `/efcore/contexts/{context}/outbox/poisoned/{id}` | One poisoned outbox row with its payload |
| POST | `/efcore/contexts/{context}/outbox/poisoned/{id}/requeue` | Requeue one row, optionally with an edited payload |
| POST | `/efcore/contexts/{context}/outbox/poisoned/requeue` | Requeue the given ids |
| POST | `/efcore/contexts/{context}/outbox/poisoned/requeue/all` | Requeue every poisoned row, in batches |
| DELETE | `/efcore/contexts/{context}/outbox/poisoned/{id}` | Delete one row |
| DELETE | `/efcore/contexts/{context}/outbox/poisoned` | Delete the ids in the request body |
| DELETE | `/efcore/contexts/{context}/outbox/poisoned/all` | Delete every poisoned row |
| GET | `/efcore/contexts/{context}/inbox/poisoned` | Poisoned inbox handler statuses |
| GET | `/efcore/contexts/{context}/inbox/poisoned/{handlerStatusId}` | One poisoned handler status |
| POST | `/efcore/contexts/{context}/inbox/poisoned/{handlerStatusId}/requeue` | Requeue one handler status |
| POST | `/efcore/contexts/{context}/inbox/messages/{messageId}/requeue` | Requeue every poisoned handler of one message |
| GET | `/efcore/contexts/{context}/inbox/messages/{messageId}/handlers` | Handler statuses of one message |
| POST | `/efcore/contexts/{context}/inbox/poisoned/requeue` | Requeue the given handler status ids |
| POST | `/efcore/contexts/{context}/inbox/poisoned/requeue/all` | Requeue every poisoned handler status |
| DELETE | `/efcore/contexts/{context}/inbox/poisoned/{handlerStatusId}` | Delete one handler status |
| DELETE | `/efcore/contexts/{context}/inbox/poisoned` | Delete the ids in the request body |
| DELETE | `/efcore/contexts/{context}/inbox/poisoned/all` | Delete every poisoned handler status |

Bulk delete carries its ids in the **request body of a `DELETE`**. `/poisoned` and `/poisoned/all`
are deliberately separate routes so that an intermediary stripping the body cannot turn "delete
these five" into "delete everything".

## Runnable example

`examples/` wires this up end to end: the playground host serves the dashboard for its own two
DbContexts and aggregates a second service (`examples/InventoryService`) that hosts no UI. See
[examples/README.md](../examples/README.md).

## What's Next

- [Operations Guide](operations.md) — investigating and retrying poisoned messages by hand
- [Observability](observability.md) — metrics and tracing
- [Outbox](outbox.md) / [Inbox](inbox.md) — what the poison state means
