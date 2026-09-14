# Management Control Plane Rebuild

Supersedes `plans/management-ui-architecture-plan.md`. That plan described the right target
shape, but PR 2 (shared operation layer) was never executed and the RabbitMQ provider in PR 4
was built on a primitive that cannot work under the required broker permissions. Delete the old
plan file when this one is accepted.

## Status

- **Revision 2.** Incorporates an external review pass. Findings on control-plane caller
  authentication, boot-order 404s, unroutable instance commands, multi-batch idempotency,
  antiforgery scope, dashboard schema creation, retention, execution scoping, query cost, and
  payload encoding were accepted and are addressed in sections 2.4 to 2.11. Two review findings
  were corrected rather than adopted verbatim, and are noted inline.
- **Target:** the management/dashboard feature across `Ratatoskr.Management*`, `Ratatoskr.UI`,
  `Ratatoskr/Management`, `Ratatoskr.EfCore/Management`, `Ratatoskr.RabbitMq/Management`.
- **Decision:** rebuild the control plane; salvage the EF Core query/mutation logic, the HTML/CSS,
  and the package layering concept.
- **Library state:** pre-alpha, no users. No compatibility shims, no obsolete aliases, no
  migration paths. Break freely.

---

## 1. Findings

### 1.1 The RabbitMQ provider cannot work with the required permissions

Required permissions for a user `u`:

| Permission | Pattern |
|---|---|
| configure | `{u}\..*` |
| write | `{u}\..*\|.*\.inbox$` |
| read | `{u}\..*\|.*(?<!internal)$` |

The current provider publishes every management message to the **default exchange** with
`routingKey = queueName` (`RabbitMqManagementRuntime.ExecuteAsync`, `PublishAsync`,
`RabbitMqManagementCommandConsumer.HandleAsync`). In RabbitMQ, `basic.publish` is authorised
against the *exchange*, and the default exchange is `amq.default`, which matches neither pattern.

Verified on `rabbitmq:4.3.5` with users `orders` and `dashboard` carrying exactly those patterns:

| Attempt | Result |
|---|---|
| `orders` publishes to default exchange, rk `dashboard.management.discovery.inbox` | **DENIED** `403 ACCESS_REFUSED - write access to exchange 'amq.default' refused` |
| `dashboard` publishes to default exchange, rk `orders.management.commands.inbox` | **DENIED** `403 ACCESS_REFUSED` |
| `orders` declares server-named (`amq.gen-*`) exclusive queue | **DENIED** `403 ACCESS_REFUSED - configure access to queue 'amq.gen-...'` |
| `dashboard` declares transient non-exclusive queue | **DENIED** `541 INTERNAL_ERROR - Feature 'transient_nonexcl_queues' is deprecated` |

The `.*\.inbox$` write rule grants publish rights to **exchanges** whose name ends in `.inbox`.
That is the only cross-identity publish channel available. The entire naming scheme must move
from "queues addressed through the default exchange" to "receiver-owned `*.inbox` exchanges".

A topology that does work, verified end to end in the same experiment:

| Attempt | Result |
|---|---|
| `orders` declares durable queue `orders.mgmt.commands.q` | ALLOWED |
| `orders` declares direct exchange `orders.mgmt.commands.inbox` and binds its queue | ALLOWED |
| `dashboard` publishes a command to `orders.mgmt.commands.inbox` | ALLOWED, delivered |
| `dashboard` declares direct exchange `dashboard.mgmt.replies.inbox` | ALLOWED |
| `dashboard` declares **named** exclusive queue `dashboard.replies.r1`, binds rk `r1` | ALLOWED |
| `orders` publishes a reply to `dashboard.mgmt.replies.inbox` rk `r1` | ALLOWED, delivered |
| `dashboard` declares fanout `dashboard.mgmt.discovery.inbox` | ALLOWED |
| two dashboard replicas bind named exclusive queues to it | ALLOWED |
| `orders` publishes one heartbeat, **both** replicas receive it | ALLOWED |

A second experiment, run after review feedback, covers sender authenticity, boot ordering, and
unroutable commands:

| Attempt | Result |
|---|---|
| `attacker` (an ordinary vhost identity with the same permission set) publishes an **anonymous** command to `orders.mgmt.cmd.inbox` | **ALLOWED, and delivered to the agent's command queue** |
| `attacker` publishes a command with a spoofed `user_id: dashboard` | **DENIED** `406 PRECONDITION_FAILED - user_id property set to 'dashboard' but authenticated user was 'attacker'` |
| `dashboard` publishes with a truthful `user_id: dashboard` | ALLOWED, `user_id` readable by the agent |
| `orders` publishes a heartbeat to `dashboard.mgmt.discovery.inbox` before the dashboard has declared it | **DENIED** `404 NOT_FOUND`, **and the channel is closed and unusable afterwards** |
| `dashboard` publishes `mandatory: true` to rk `inst.dead-replica` with no binding | returned as `312 NO_ROUTE` |
| `dashboard` publishes `mandatory: true` to rk `svc.orders` (queue exists, no consumer) | not returned, which is the desired behaviour |

Rules that fall out and must be encoded in the design:

1. Never publish to the default exchange.
2. Cross-identity delivery goes to an exchange named `{receiverPrefix}....inbox`, declared by the
   receiver.
3. Never use server-named queues. Always name them `{ownPrefix}....`.
4. Never declare a transient non-exclusive queue. Use durable, or exclusive.
5. Read permission is broad (`.*(?<!internal)$`), so any identity can consume most queues.
   Management payloads can contain message bodies and exception detail, so the control plane
   must assume the broker is not a confidentiality boundary and say so in the docs.
6. Write permission is equally broad. **Any identity in the vhost can publish a command to any
   `*.inbox` exchange**, verified above. The broker is not an authentication boundary either, so
   agents must authenticate the caller themselves. See section 2.10.
7. The broker validates the `user_id` message property against the authenticated connection, so
   `user_id` is a trustworthy sender identity that costs no shared secret. It is absent unless the
   publisher sets it, so "absent" must be treated as untrusted rather than as trusted.
8. A publisher cannot declare a receiver-owned exchange, so it will inevitably publish to a
   missing exchange whenever the receiver has not booted yet. That closes the channel. Heartbeat
   publishing must therefore own a channel it can afford to lose, and retry with backoff.

### 1.2 Other correctness defects

| # | Defect | Location |
|---|---|---|
| 1 | Transient non-exclusive discovery queue; breaks on RabbitMQ 4.1+. Tests pin `rabbitmq:4.0-alpine` so CI hides it. | `RabbitMqManagementRuntime.ExecuteAsync` |
| 2 | All dashboard replicas consume one shared discovery queue, so each heartbeat reaches exactly one replica. The code comment claims fanout. | `RabbitMqManagementResourceNames.LocalDiscoveryInbox` |
| 3 | `_receivesDiscovery = commandDispatcher is null`: a process that is both agent and dashboard receives no discovery at all. This is the "dashboard in the same app" case. | `RabbitMqManagementRuntime` |
| 4 | In-process announcement is published exactly once at startup; the registry prunes after 45s. An in-process dashboard goes blank 45 seconds after start. | `InProcessManagementAnnouncementService`, `ServiceRegistry` |
| 5 | Only one management provider can exist: `AddRabbitMqManagement` calls `services.Replace` on singleton `IManagementClient`/`IServiceCatalog`. Two brokers, or broker + in-process, is structurally impossible. | `RabbitMqManagementServiceCollectionExtensions` |
| 6 | Management is welded to the app's broker connection (`RabbitMqConnectionManager`, single non-keyed `RabbitMqOptions`). A separate management broker cannot be configured, and a dashboard-only process must register the whole messaging stack to get a connection. | `RabbitMqManagementRuntime` ctor |
| 7 | Duplicate-command suppression uses an unbounded, process-local `ConcurrentDictionary<string, Task<...>>`. Memory leak, and no idempotency across restarts or replicas. | `RabbitMqManagementCommandConsumer._completed` |
| 8 | Logical-service targeting is "whichever replica heartbeated last" (single `_commandEndpoints` entry per service, instance-specific queue). Requests go to a dead queue until the next heartbeat. | `RabbitMqManagementRuntime.OnReceivedAsync` |
| 9 | `IManagementClient` throws `InvalidOperationException` on any failure, discarding the structured error code the protocol defines. | both client implementations |
| 10 | Bulk operations are unconditional "all poisoned", unbounded, no preview, while the UI tells the operator it matches "the current explicit poisoned filter". | `EfCoreManagementOperations.Bulk*`, `app.js:bulk` |
| 11 | Mutating endpoints call `DisableAntiforgery()` while supporting cookie auth. | `RatatoskrUiEndpointExtensions`, `ManagementApiEndpointExtensions` |

### 1.3 Structural and maintainability defects

- **Two complete parallel management stacks.** `Ratatoskr.EfCore/Management/Endpoints/*` (HTTP:
  filtering, date range, search, bulk-by-ids, server-side batching, `DbUpdateConcurrencyException`
  handling, ProblemDetails, `CursorHelper`) and `EfCoreManagementOperations` (control plane: none of
  that, `ManagementCursor`, opposite sort order). Both are mapped side by side in
  `examples/PlaygroundHost/Program.cs`.
- **`Ratatoskr.Management` hard-references `Ratatoskr.EfCore`.** The transport-neutral core is
  storage-bound; the planned `Ratatoskr.Management.EfCore` package does not exist.
- **Dead contracts:** `IManagementCapabilityContributor`, `IManagementTopologyContributor`,
  `IManagementOperationHandler<TRequest,TResponse>`, `ServiceInstanceAnnouncement`,
  `CapabilityDescriptor`, `ManagementActor`, `AcceptedOperationId`, `ManagementResponseStatus.Accepted`.
  Declared, never implemented or populated.
- **Dead options:** `RatatoskrManagementOptions.HeartbeatInterval`, `.EnableHeartbeat`,
  `RatatoskrUiOptions.ServiceOfflineThreshold`, `RabbitMqManagementOptions.ConsumerConcurrency`.
  Set by examples and tests, read by nothing.
- **File/type mismatches:** `ManagementRequestDispatcher.cs` declares `ManagementRequestHandler`;
  `ManagementRequestHandler.cs` declares `EfCoreManagementOperations` (543 lines).
- **`ManagementPayload(Type, Json, ContentType)`** is constructed everywhere as
  `new ManagementPayload("application/json", json)`; the type slot holds a content type.
- **Style divergence.** The management and RabbitMQ packages and `wwwroot/js/app.js` are written
  as 200-400 character single lines with no blank lines, in a repo whose other code is carefully
  formatted and commented.
- **Docs describe features that do not exist:** audit records, bounded and filtered bulk
  operations, fanout discovery, `ExchangePrefix`. `MapRatatoskrManagementApi` is a public API with
  no entry in `docs/`.
- **Test blind spots:** no least-privilege broker test, no multi-replica test, no multi-transport
  test, no time-advance test (which is why defect 4 is invisible), RabbitMQ image pinned below the
  version that enforces the deprecation in defect 1.

### 1.4 What is worth keeping

| Keep | Why |
|---|---|
| Package layering concept (Abstractions / core / provider / UI) | Correct shape; only the contents are wrong. |
| `Ratatoskr.EfCore/Management/Endpoints/*` query and mutation logic | Keyset pagination, filters, search, bounded batches, concurrency handling. The stronger of the two implementations. |
| `EfCoreManagementDbContextLookup` and descriptor model | Clean, validates duplicate short names at startup. |
| `ServiceRegistry` immutability and out-of-order rejection | Sound; needs to move behind the persistent store. |
| `wwwroot/index.html`, `wwwroot/css/dashboard.css` | Layout and styling are fine. |
| SSE bounded-channel coalescing and snapshot-on-update approach | Correct approach; keep the shape. |
| CSP, `nosniff`, split authorization policies | Keep as is. |

---

## 2. Target architecture

### 2.1 Decisions taken

| Decision | Choice |
|---|---|
| Rework scope | Rebuild control plane; salvage EF Core operation logic and HTML/CSS. |
| Per-service HTTP API | **Keep** as a documented, public, per-service REST API, rebuilt on the shared operation layer. |
| Management broker connection | Management owns its own connection configuration. Core named-transport support for app messaging is a separate, later workstream. |
| Frontend | Zero-npm vanilla JS, rewritten readably. |
| RabbitMQ identity model | Varies per deployment. Resource prefix is explicitly configurable, defaults to the connection user name, and nothing assumes prefix maps 1:1 to a service. |
| Bulk and long-running ops | Bounded synchronous only: explicit filter or id list, hard caps, count-first preview. No async job model in v1. |
| Mutation idempotency | Persisted operation ids in the managed service's own DbContext, written in the same transaction as the mutation. |
| Dashboard persistence | **Required** EF Core DbContext for the dashboard (service registry + audit log). |

Note on the last one: requiring a database raises the setup cost for someone who only wants to
look at a dashboard. It was chosen deliberately over the optional variant, and the plan below
keeps the registration to a single call plus a provider choice. SQLite is supported for the
trivial case.

### 2.2 Package graph

```
Ratatoskr.Management.Abstractions      no dependencies
  protocol envelopes, errors, targets, announcements, topology, capabilities
  operation and transport interfaces

Ratatoskr.Management                   -> Abstractions, Ratatoskr
  operation registry and dispatcher
  agent host, capability and topology contributors
  multi-transport runtime and transport registry
  in-process transport
  shared ASP.NET route tree + ProblemDetails mapping

Ratatoskr.Management.EfCore            -> Ratatoskr.Management, Ratatoskr.EfCore
  inbox/outbox query and mutation operations (the single implementation)
  operation-idempotency entity and store
  per-service REST API endpoints
  EF Core capability and DbContext topology contributors

Ratatoskr.Management.RabbitMq          -> Ratatoskr.Management, RabbitMQ.Client
  control-plane transport: own connection, own topology
  NO dependency on Ratatoskr.RabbitMq

Ratatoskr.UI                           -> Ratatoskr.Management, Microsoft.EntityFrameworkCore
  dashboard DbContext (registry + audit)
  REST facade over the transport registry, SSE
  embedded static assets
```

`Ratatoskr.Management.RabbitMq` deliberately does not reference `Ratatoskr.RabbitMq`. A dashboard
process then needs no messaging stack at all, and management can point at a different broker with
different credentials. Same-broker deployments simply pass the same connection string, which in
practice comes from the same configuration key.

`Ratatoskr` core loses `Management/` entirely (`IRatatoskrEndpointConfigurator`,
`ManagementApiEndpointExtensions`, `ManagementResults`, `PaginationOptions` move into
`Ratatoskr.Management`). `Ratatoskr.EfCore` loses `Management/` entirely (moves into
`Ratatoskr.Management.EfCore`). `Ratatoskr.RabbitMq/Management/` is deleted; the RabbitMQ health
endpoint is already covered by `AddRatatoskrRabbitMq()` health checks.

### 2.3 Multi-transport model

Service identity at the dashboard is `(TransportName, ServiceName, InstanceId)`. This is what makes
two RabbitMQ brokers, or RabbitMQ plus in-process, work simultaneously, and it is the migration
story for a team moving between clouds.

```csharp
// Dashboard
services.AddRatatoskrDashboard(dashboard =>
{
    dashboard.UseStore(db => db.UseNpgsql(dashboardCs));
    dashboard.AddRabbitMq("eu", o => { o.ConnectionString = euCs;  o.ResourcePrefix = "dashboard"; });
    dashboard.AddRabbitMq("us", o => { o.ConnectionString = usCs;  o.ResourcePrefix = "dashboard"; });
    dashboard.AddInProcess();                 // co-hosted services in this process
});

// Managed service; may announce over two brokers during a migration
services.AddRatatoskrManagementAgent(agent =>
{
    agent.ServiceName = "orders";
    agent.InstanceId  = Environment.MachineName;
    agent.AddRabbitMq("eu", o => { o.ConnectionString = euCs; o.DiscoveryExchange = "dashboard.mgmt.discovery.inbox"; });
    agent.AddRabbitMq("us", o => { o.ConnectionString = usCs; o.DiscoveryExchange = "dashboard.mgmt.discovery.inbox"; });
});
```

Core abstractions:

```csharp
/// One configured connection to one control plane. Registered per name.
public interface IManagementTransport
{
    string Name { get; }
    ManagementTransportCapabilities Capabilities { get; }   // e.g. supports instance targeting
    Task<ManagementResponseEnvelope> SendAsync(ManagementAddress address,
                                               ManagementRequestEnvelope request,
                                               CancellationToken ct);
}

/// Discovery feed for one transport.
public interface IManagementDiscoverySource
{
    string TransportName { get; }
    IAsyncEnumerable<ServiceAnnouncement> ListenAsync(CancellationToken ct);
}

/// Dashboard-side aggregation over every registered transport.
public interface IManagementTransportRegistry
{
    IReadOnlyList<string> TransportNames { get; }
    IManagementTransport Get(string name);
}
```

`ManagementAddress` is the transport-owned, opaque routing information learned from a service
announcement (for RabbitMQ: command exchange plus service and instance routing keys). It never
appears in the dashboard API.

### 2.4 Protocol cleanup

- One announcement type, `ServiceAnnouncement`. Delete `ServiceInstanceAnnouncement`.
- `ManagementPayload` is replaced by a `JsonElement?` payload plus the existing `Operation` name,
  which already determines the shape. Deliberately not `ReadOnlyMemory<byte>`: `System.Text.Json`
  base64-encodes byte payloads, so a JSON body would arrive as base64 nested inside JSON, roughly a
  third larger and encoded and decoded twice on every hop.
- `Actor` is populated from `HttpContext.User` at the REST facade and the per-service API, carried
  on the wire, and written to both the dashboard audit log and the agent's idempotency record.
- Drop `ManagementResponseStatus.Accepted` and `AcceptedOperationId`. Reintroduce with a minor
  protocol bump if an async job model is ever needed.
- `Capabilities` is actually populated from `IManagementCapabilityContributor`, so the dashboard
  can hide tabs a service does not support. This is the extension point that lets a future Kafka or
  Service Bus agent advertise a different feature set without a UI change.
- All results are `ManagementResult<T>` carrying `Ok | NotFound | Invalid | Conflict | Forbidden |
  Unsupported` plus a stable code. No `InvalidOperationException` as an error channel.
- One canonical cursor: `[version:1][utcTicks:8 BE][guid:16]`, base64url, ascending
  `(CreatedAt, Id)`. Delete `CursorHelper` and `ManagementCursor`.

### 2.5 Shared operation layer

One implementation per operation, reached by three callers: the per-service REST API, the
in-process transport, and a broker transport.

```csharp
public interface IManagementOperation
{
    string Name { get; }                        // "outbox.list", "inbox.requeue", ...
    Type RequestType { get; }
    Task<ManagementResult> ExecuteAsync(ManagementOperationContext context, CancellationToken ct);
}
```

`ManagementOperationContext` carries the resolved resource (DbContext name), the actor, the
operation id, and the deadline.

Operation set for v1:

| Operation | Notes |
|---|---|
| `service.describe` | Counts, DbContexts, channels, capabilities. Also the heartbeat body. |
| `contexts.list` | Names, has-outbox, has-inbox. |
| `context.health` | Backlog gauges plus last successful processing timestamps. |
| `outbox.list` / `inbox.list` | status, from, to, search, cursor, limit. Keyset, ascending. |
| `outbox.get` / `inbox.get` | Payload and CloudEvents metadata. Gated by the payloads policy. |
| `outbox.count` / `inbox.count` | Same filter as list. Backs the destructive-action preview. |
| `outbox.requeue` / `inbox.requeue` | Explicit id list, max 1000, batched, concurrency-aware. |
| `outbox.delete` / `inbox.delete` | Same. Inbox delete keeps the orphan-message cleanup rule. |
| `inbox.requeueMessage` | All poisoned handlers of one message id. |
| `outbox.requeueMatching` / `outbox.deleteMatching` | Explicit filter required, server-batched 500, capped by `MaxTotalOperations` (default 10000), deadline-aware, returns `{processed, remaining, capped}`. |
| `inbox.requeueMatching` / `inbox.deleteMatching` | Same. |

No unbounded "apply to everything" variant exists. A filter is mandatory; an empty filter is
rejected as `Invalid`.

Query-cost rules, because these tables are retained and can grow large:

- `*.list` returns **no total count**. Keyset pagination does not need one, and the
  `LongCountAsync` over the whole filtered set is the most expensive part of the current endpoint.
  Totals come only from the explicit `*.count` operation, which the preview flow already calls.
- `search` is a `LIKE` over `SerializedProperties` with a leading wildcard, so it cannot use an
  index. When `search` is supplied the request must also constrain the scan: either a bounded
  `from`/`to` window, or a status other than "all". An unbounded search is rejected as `Invalid`.
- Every management query runs under a configurable command timeout, defaulting to less than the
  protocol deadline, so a slow scan fails as `deadline_exceeded` instead of pinning a connection
  until the caller gives up.
- The docs state plainly that search is a substring match over serialized CloudEvents properties,
  not a full-text index.

### 2.6 Idempotency

```csharp
internal sealed class ManagementOperationEntity   // configured via AddRatatoskrEfCoreModel
{
    public Guid OperationId { get; set; }         // PK, supplied by the caller
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Operation { get; set; }
    public string? Actor { get; set; }
    public ManagementOperationState State { get; set; }   // InProgress | Completed
    public string? FilterJson { get; set; }       // matching ops only, for resume validation
    public int ProcessedCount { get; set; }       // matching ops only, accumulated across batches
    public string? ResultJson { get; set; }       // set when State == Completed
}
```

**Single-row operations** (`requeue`, `delete`, `requeueMessage`) insert this row with
`State = Completed` inside the same `SaveChangesAsync` as their entity changes. A duplicate
delivery hits the primary key, the operation short-circuits, and the stored result is replayed.

**Matching operations** commit in batches of 500, so a single-commit record does not work: writing
it first would mark a partially-applied operation as done, and writing it last would leave a
crash-interrupted run with no record at all. They therefore track progress:

1. Insert the row with `State = InProgress`, the serialized filter, and `ProcessedCount = 0`.
2. Each batch commits its entity changes **and** the `ProcessedCount` increment in one
   transaction.
3. The final batch sets `State = Completed`, `CompletedAt`, and `ResultJson`.

On duplicate delivery: `Completed` replays `ResultJson`; `InProgress` resumes and keeps
accumulating into the same row; a mismatched `FilterJson` for a known operation id is rejected as
`Conflict` rather than silently applying a different mutation. Resume is safe because these
mutations narrow their own predicate. A requeued row is no longer poisoned and a deleted row is
gone, so re-running the same filter picks up only what is left. Progress tracking exists to make
the **reported count** accurate and the operation attributable, not to prevent double mutation.

Retention is handled by a dedicated `ManagementOperationCleanupService<TDbContext>` with its own
retention option and distributed lock name, registered whenever the EF Core management package is
used. It is deliberately **not** attached to `InboxCleanupService`, which only runs when the inbox
is enabled and a retention period is configured. An outbox-only service must still clean up its
operation log.

### 2.7 RabbitMQ topology

Everything below is permitted under the required permission patterns; see section 1.1.

| Owner | Resource | Kind | Purpose |
|---|---|---|---|
| Agent | `{prefix}.mgmt.cmd.inbox` | direct, durable | Command entry point. Ends in `.inbox`, so any identity may publish. |
| Agent | `{prefix}.mgmt.cmd.{service}.q` | durable queue, bound rk `svc.{service}` | Logical-service targeting. Replicas compete, so exactly one handles each command. |
| Agent | `{prefix}.mgmt.cmd.{service}.{instance}.q` | exclusive queue, bound rk `inst.{instance}` | Instance targeting. Auto-removed when the replica disconnects. |
| Dashboard | `{dashPrefix}.mgmt.discovery.inbox` | fanout, durable | Heartbeat sink. Agents are configured with this name. |
| Dashboard | `{dashPrefix}.mgmt.discovery.{replica}.q` | exclusive queue | Every replica receives every heartbeat. Fixes defect 1.2/2. |
| Dashboard | `{dashPrefix}.mgmt.reply.inbox` | direct, durable | Reply sink. |
| Dashboard | `{dashPrefix}.mgmt.reply.{replica}.q` | exclusive queue, bound rk `{replica}` | No reply stealing between replicas. |

Rules:

- `ResourcePrefix` is explicit configuration, defaulting to the connection user name. Nothing
  assumes it maps to one service, so several services sharing one identity remain addressable
  through the `{service}`/`{instance}` routing-key components.
- A request carries its reply address (`exchange` + `routingKey`) in the envelope, so no reply
  configuration is needed on the agent.
- Never declare server-named queues; never declare transient non-exclusive queues; never publish
  to the default exchange. A startup self-check asserts all three.
- Commands and replies are persistent with `expiration` set to the request deadline. Publisher
  confirms are enabled for both.
- Prefetch and consumer concurrency are bounded and actually used (unlike today's
  `ConsumerConcurrency`).
- Malformed messages are nacked without requeue and counted on a metric; they never poison the loop.
- A command is acked only after its response has been confirmed, or after the idempotency row is
  committed. A crash in between replays safely because of section 2.6.
- Logical-service addressing does not depend on the last heartbeat: all replicas of a service
  advertise the same exchange and `svc.` routing key, so a dead replica cannot capture traffic.
- **Commands publish with `mandatory: true` and the client handles `basic.return`.** An
  instance-targeted command whose replica is gone is returned as `312 NO_ROUTE` and fails
  immediately with a stable `target_unreachable` code instead of hanging until the deadline.
  Verified in section 1.1 that this fires for a missing instance binding and does not fire for the
  durable service queue with no consumer, which is the behaviour we want in both cases.
- **Channels are separated by blast radius.** Heartbeat publishing, reply publishing, and command
  consumption each own a channel. Publishing to a receiver-owned exchange that does not exist yet
  closes the channel (verified: `404 NOT_FOUND`, channel unusable afterwards), which must never
  take down command consumption or in-flight replies.
- **Missing-exchange tolerance.** Publishing a heartbeat or a reply to an exchange that is not
  declared yet is an expected boot-ordering state, not an error. The publisher catches the 404,
  logs once at warning, reopens the channel, and retries with capped exponential backoff. An agent
  that starts before its dashboard must keep serving commands and start being discovered as soon
  as the dashboard declares its discovery exchange.
- **Recovery is explicit and tested.** `RabbitMQ.Client` already recovers topology when enabled, so
  the gap is that the plan never said so and never tested it, not that recovery is unavailable.
  The management connection enables automatic connection and
  topology recovery, and the declare/bind routine is re-run idempotently on recovery as a safety
  net. Exclusive reply and discovery queues do not survive a disconnect, so on connection loss all
  pending requests fail deterministically with `transport_unavailable` rather than waiting for
  their deadline against a queue that no longer exists.

### 2.8 Dashboard store

```csharp
public sealed class RatatoskrDashboardDbContext : DbContext
{
    public DbSet<DashboardServiceSnapshot> ServiceSnapshots { get; }   // transport+service+instance, announcement JSON, timestamps
    public DbSet<DashboardAuditEntry>      AuditEntries     { get; }   // actor, operation id, transport, service, instance, operation, filter/ids, started, completed, outcome
}
```

Registered with `dashboard.UseStore(db => db.UseNpgsql(...))`; `UseSqlite` is supported and
documented for single-node or local use.

**Schema creation ships as EF Core migrations**, not `EnsureCreated`. This context is owned by
Ratatoskr, unlike the inbox/outbox model which is embedded in the user's own context, so we can and
should ship migrations for it. `EnsureCreated` is all-or-nothing per database and silently creates
nothing when the database already exists, which is exactly the common case of pointing the
dashboard at an existing application database. The options are a documented `MigrateAsync` call at
startup or an opt-in `AutoMigrate` flag, the tables live under a configurable schema
(default `ratatoskr_dashboard`) so they cannot collide with application tables, and the docs say
explicitly not to use `EnsureCreated`.

`AuditEntries` grows without bound by construction, so the dashboard runs an audit retention
worker with a configurable period (default 90 days) and deletes in bounded batches. Retention is
part of the required-store decision, not an afterthought.

Benefits that justify the required dependency:

- The dashboard shows last-known topology immediately after a restart instead of an empty page
  until the next heartbeat, and marks entries stale by age.
- Mutations are auditable and queryable in the UI, which is what the current docs already promise.
- The in-memory registry becomes a write-through cache over the store, which removes defect 1.2/4
  (the in-process dashboard blanking after 45 seconds) as a class of bug.

The in-memory layer keeps `ServiceRegistry`'s immutable snapshots, out-of-order rejection, and
`TimeProvider` usage.

### 2.9 HTTP surfaces

Two surfaces, one route tree, one set of DTOs.

- **Per-service REST API**, `MapRatatoskrManagementApi(policies, "/ratatoskr/api/v1")`, from
  `Ratatoskr.Management.EfCore`. Dispatches locally. Public and documented.
- **Dashboard facade**, `MapRatatoskrUI(policies, "/ratatoskr")`, from `Ratatoskr.UI`. Same
  resource tree under `/api/transports/{transport}/services/{service}/...`, dispatching through
  the transport registry.

Both are produced from a shared `ManagementApiRoutes.Map(group, IManagementDispatchStrategy)` so a
new operation appears on both surfaces at once. Both map `ManagementResult` to ProblemDetails
through the same translator.

Security changes: remove the blanket `DisableAntiforgery()`. Antiforgery applies **only to
requests authenticated with ambient credentials**, meaning a cookie scheme. An endpoint filter
inspects how the principal was authenticated: cookie-authenticated requests must carry a valid
antiforgery token, which `index.html` issues and the client sends as a header; requests
authenticated by bearer token, API key, mTLS, or any other explicit-credential scheme skip the
check, because there is no CSRF exposure without ambient credentials. Applying antiforgery
globally would break exactly the programmatic callers the per-service REST API exists to serve.

Keep the split policies, the CSP, and `nosniff`. Document explicitly that broker read permissions
are broad, so management payloads are visible to any identity with read access.

### 2.10 Control-plane authentication and integrity

The permission model makes every `*.inbox` exchange writable by every identity in the vhost.
Section 1.1 confirms an ordinary identity can inject an anonymous command into another service's
command exchange and have it delivered. The dashboard's authorization policies live at its HTTP
edge and protect nothing on the broker path, so **an agent must authenticate its caller**. Without
this, any compromised or merely careless service in the vhost can delete another service's outbox.

Agents reject any command that fails caller authentication, before dispatch, with
`unauthenticated_caller`, and count it on a metric. Three mechanisms, in order of preference:

1. **Validated `user_id` allowlist (default).** The broker refuses a publish whose `user_id`
   property does not match the authenticated connection, verified in section 1.1 with a
   `406 PRECONDITION_FAILED`. Agents require the property to be present and to appear in a
   configured `AllowedCallers` list. Zero shared secrets, zero key rotation, broker-enforced. The
   dashboard sets `user_id` on every command it publishes. An absent `user_id` is untrusted and
   rejected.
2. **Signed envelopes (for shared identities).** Where several services and the dashboard
   authenticate as one RabbitMQ user, `user_id` no longer distinguishes callers. Those deployments
   configure a shared management secret; commands carry an HMAC over the canonical envelope bytes
   plus the operation id and deadline, and agents reject unsigned, mis-signed, or expired
   envelopes. The operation-id idempotency record doubles as replay protection.
3. **A dedicated management vhost.** The strongest option where operators control provisioning,
   and worth recommending in the docs, but it cannot be assumed given the stated permission model.

`AllowedCallers` (or the secret) is required configuration with no insecure default. Startup fails
if neither is configured and the transport is a broker, because "accept anything from the broker"
must never be reachable by omission.

### 2.11 Execution scoping

The existing `EfCoreManagementOperations` already creates a scope per operation, so this is not a
defect in the current code. It is a rule the rebuild must carry forward explicitly, because the
new command consumers are singletons and the failure mode is silent.

Command consumers are singleton background services, and management operations touch a scoped
`DbContext`. Every command execution therefore creates an `AsyncServiceScope`, resolves the
operation and its `DbContext` from that scope, and disposes it when the command completes. No
operation, dispatcher, or lookup that touches a `DbContext` is registered as a singleton, and none
captures the root provider. This is enforced by building the container with
`ValidateScopes = true` and `ValidateOnBuild = true` in the test host, so a captive dependency
fails a test rather than surfacing as an intermittent production fault.

---

## 3. Delivery plan

Seven phases. Each is independently reviewable, leaves the solution building and green, and ends
with the stated tests passing. Test commands follow `CLAUDE.md` (TUnit, `dotnet run`).

### Phase 0: Test harness prerequisites

Do this first so the rest of the work is actually verifiable.

- Bump `RabbitMqContainerFixture` from `rabbitmq:4.0-alpine` to `rabbitmq:4.1-alpine` or newer so
  the `transient_nonexcl_queues` deprecation is enforced in CI.
- Add `RestrictedRabbitMqFixture`: provisions users with the exact required permission patterns
  (`{u}\..*`, `{u}\..*|.*\.inbox$`, `{u}\..*|.*(?<!internal)$`) and hands out per-identity
  connection strings.
- The restricted fixture provisions at least three identities, including a third-party `attacker`
  identity used to prove command injection is rejected.
- Add a `TimeProvider`-controllable test host so heartbeat staleness and pruning can be advanced
  without real waits.
- Build every test host with `ValidateScopes = true` and `ValidateOnBuild = true` so a captive
  scoped dependency fails a test (section 2.11).
- Record the verified permission matrices from section 1.1 in `docs/rabbitmq.md`.

Exit criteria: the restricted fixture is usable from a test; probe tests demonstrate, under the
restricted identities, that publishing to the default exchange is refused, that a spoofed `user_id`
is refused, and that publishing to an undeclared exchange closes the channel.

### Phase 1: Protocol and package skeleton

- Create `Ratatoskr.Management.Abstractions` fresh: envelopes, `ManagementResult`, stable error
  codes, `ManagementTarget`, `ServiceAnnouncement`, `CapabilityDescriptor`, `ChannelTopology`,
  `TransportBinding`, cursor page contracts, `IManagementOperation`, `IManagementTransport`,
  `IManagementDiscoverySource`, `IManagementCapabilityContributor`, `IManagementTopologyContributor`.
- Delete `ServiceInstanceAnnouncement`, `ManagementPayload`, `Accepted`/`AcceptedOperationId`, and
  every transitional `[JsonIgnore]` accessor on the envelopes.
- Canonical cursor implementation with round-trip and malformed-input tests.
- JSON serialization snapshot tests for every wire type.

Exit criteria: Abstractions has zero package dependencies; serialization snapshots are committed;
unsupported protocol version, unsupported operation, and unsupported capability each have a
specified response shape covered by a test.

### Phase 2: Shared operation layer and per-service REST API

- Create `Ratatoskr.Management` with the operation registry, dispatcher, agent host, and the shared
  ASP.NET route tree plus ProblemDetails translator. Move `ManagementResults` and
  `PaginationOptions` here. Delete `Ratatoskr/Management/` from core.
- Create `Ratatoskr.Management.EfCore`. Port the query and mutation logic out of
  `Ratatoskr.EfCore/Management/Endpoints/*` into operations, preserving keyset pagination, date and
  search filters, bounded batches, `DbUpdateConcurrencyException` handling, the inbox orphan
  cleanup rule, and domain `Requeue()` invariants.
- Add the count/preview operations and the bounded `*Matching` operations with the mandatory-filter
  rule and `MaxTotalOperations` cap.
- Add `ManagementOperationEntity` to `AddRatatoskrEfCoreModel`, the idempotency store with
  in-progress tracking, and `ManagementOperationCleanupService<TDbContext>`.
- Apply the query-cost rules from section 2.5: no total count on list, constrained search, command
  timeout.
- Rebuild `MapRatatoskrManagementApi` on the shared route tree, now in `Ratatoskr.Management.EfCore`.
- Delete `Ratatoskr.EfCore/Management/` and `EfCoreManagementOperations`.

Exit criteria: one implementation of every inbox/outbox operation; existing HTTP management tests
pass against the rebuilt API with their assertions unchanged except for route/DTO renames; a
duplicate operation id applied twice mutates once and returns the same result both times; a
matching operation interrupted mid-batch and redelivered resumes rather than restarting, and
reports an accurate total; the same operation id replayed with a different filter is rejected as
`Conflict`; a bulk request without a filter and without ids is rejected; `MaxTotalOperations` is
observed; an unbounded `search` is rejected; the operation log is pruned in an **outbox-only**
service with no inbox configured.

### Phase 3: Multi-transport runtime and in-process transport

- Transport registry, `AddRatatoskrManagementAgent`, `AddRatatoskrDashboard` builders, options
  validation on start (service name, instance id, timeouts, heartbeat intervals, duplicate
  transport names).
- In-process transport and discovery source, with a periodic announcement loop driven by
  `TimeProvider` (fixes defect 1.2/4).
- A `FakeManagementTransport` plus a reusable provider conformance suite: discovery snapshot,
  out-of-order announcement rejection, logical vs instance targeting, concurrent correlation with
  two dashboard replicas, cancellation and deadline behaviour, duplicate delivery, malformed
  message handling, bounded memory for slow consumers, deterministic shutdown.

Exit criteria: the in-process transport passes the conformance suite; a dashboard with two
transports registered lists services from both; an in-process dashboard still shows its service
after the test clock advances past several heartbeat intervals; registering two transports under
the same name fails at startup with a clear message.

### Phase 4: RabbitMQ control-plane transport

- Rebuild `Ratatoskr.Management.RabbitMq` against `RabbitMQ.Client` only, with its own connection,
  its own options, and the topology from section 2.7.
- Startup self-check asserting no default-exchange publish, no server-named queue, no transient
  non-exclusive queue.
- Publisher confirms, bounded prefetch and concurrency, malformed-message policy, ack-after-commit
  ordering, deadline propagation via `expiration`.
- Caller authentication per section 2.10: `user_id` allowlist by default, optional HMAC for shared
  identities, startup failure when neither is configured.
- `mandatory: true` on commands with `basic.return` handling mapped to `target_unreachable`.
- One channel each for heartbeats, replies, and command consumption; 404 tolerance with capped
  backoff on both publish paths.
- Automatic connection and topology recovery, idempotent re-declare on recovery, and deterministic
  failure of pending requests on connection loss.
- Delete `Ratatoskr.RabbitMq/Management/`.

Exit criteria: the conformance suite passes over RabbitMQ; **the full flow passes against
`RestrictedRabbitMqFixture` with the required permission patterns**; two dashboard replicas both
receive every heartbeat and never steal each other's replies; a killed replica does not capture
logical-service traffic and an instance-targeted command to it fails fast as `target_unreachable`
rather than waiting out the deadline; a command redelivered after an agent restart mutates once;
**an agent started before its dashboard keeps serving commands, logs the missing discovery exchange
once, and is discovered as soon as the dashboard declares it**; **a command published by the
`attacker` identity without a valid `user_id` is rejected before dispatch and counted**; a broker
connection drop fails pending requests deterministically and the agent recovers its topology; the
dashboard process runs without `Ratatoskr.RabbitMq` installed; a management broker distinct from
the app broker works.

### Phase 5: Dashboard store, REST facade, SSE

- `RatatoskrDashboardDbContext` with shipped EF Core migrations, a configurable schema, and an
  opt-in `AutoMigrate`; registry-as-write-through-cache; audit writer; audit retention worker.
- REST facade over the transport registry under `/api/transports/{transport}/...`, built from the
  shared route tree.
- SSE: one bounded channel and one writer loop per connection, snapshot-on-update, heartbeat
  comments, full teardown on disconnect. Never write to `HttpResponse` concurrently.
- Antiforgery scoped to cookie-authenticated requests only (section 2.9), with token issuance and
  header validation.
- Capability-aware responses so the UI can hide unsupported features.

Exit criteria: the dashboard shows last-known services immediately after restart, marked stale by
age; **migrations apply cleanly against a database that already contains application tables**;
every mutation produces an audit row with actor, operation id, transport, target, filter or ids,
timings, and outcome; audit retention prunes in bounded batches; a slow or disconnected SSE client
causes no unbounded task or memory growth; a cookie-authenticated mutation without a valid
antiforgery token is rejected **while a bearer-token mutation with no antiforgery token succeeds**;
read and mutation policies are independently testable.

### Phase 6: Frontend rewrite

- Rewrite `wwwroot/js/app.js` as small, readable ES modules under a normal line length. Keep the
  zero-npm, embedded-resource approach. Keep `index.html` and `dashboard.css`, extending them for
  the new views.
- Add the transport dimension to navigation (`transport > service > instance > context`).
- Replace the fake "current explicit poisoned filter" bulk copy with a real flow: choose a filter,
  press preview, see the count from `*.count`, confirm, see `{processed, remaining, capped}`.
- Add the audit view.
- Keep all untrusted values on `textContent`; no inline handlers; keep the CSP.

Exit criteria: hostile service names, message ids, handler keys, errors, and payload content cannot
execute script (stored-XSS test per field); pagination, filters, preview, and bulk flows are
covered by endpoint tests; the file passes a line-length and structure review.

### Phase 7: Docs, examples, cleanup

- Rewrite `docs/management-ui.md`; add a new page for the per-service REST API; update
  `docs/configuration.md`, `docs/rabbitmq.md` (least-privilege setup and the verified permission
  matrix), `docs/operations.md`, and `docs/toc.yml`.
- Remove every documented feature that does not exist, and document every feature that now does
  (audit, bounded bulk with preview, idempotency, multi-transport, capability negotiation).
- Update `examples/PlaygroundHost` (in-process dashboard) and `examples/InventoryService` plus a
  second example service to demonstrate a distributed dashboard over two transports.
- Delete every dead option and contract listed in section 1.3.
- Delete `plans/management-ui-architecture-plan.md`.

Exit criteria: docs match implementation; examples compile and are exercised by tests; no
`[Obsolete]` aliases remain anywhere in the management packages.

---

## 4. Deletion list

Deleted outright, not migrated:

```
src/Ratatoskr/Management/                                   (moves to Ratatoskr.Management)
src/Ratatoskr.EfCore/Management/                            (moves to Ratatoskr.Management.EfCore)
src/Ratatoskr.RabbitMq/Management/                          (health checks already cover this)
src/Ratatoskr.Management/Agent/ManagementRequestHandler.cs   (EfCoreManagementOperations)
src/Ratatoskr.Management/Agent/ManagementRequestDispatcher.cs
src/Ratatoskr.Management/Agent/EfCoreManagementOperationHandlers.cs
src/Ratatoskr.Management/Agent/ManagementCursor.cs
src/Ratatoskr.Management/Runtime/InProcessManagementRuntime.cs
src/Ratatoskr.Management.RabbitMq/                          (all of it)
src/Ratatoskr.Management.Abstractions/Protocol/              (rewritten)
src/Ratatoskr.UI/Endpoints/RatatoskrUiEndpointExtensions.cs  (rewritten)
src/Ratatoskr.UI/wwwroot/js/app.js                           (rewritten)
plans/management-ui-architecture-plan.md
```

Removed public API: `RatatoskrUiOptions.ServiceOfflineThreshold`,
`RatatoskrManagementOptions.HeartbeatInterval`, `.EnableHeartbeat`,
`RabbitMqManagementOptions.ExchangePrefix`, `.ConsumerConcurrency`, `.UiInstanceId`,
`IManagementClient`, `IServiceCatalog`, `IManagementCommandHost`, `IManagementEventPublisher`,
`IManagementEventSource`, `IManagementOperationHandler<TRequest,TResponse>`,
`ManagementRequestHandler`, `ManagementOperationDispatcher`, `InProcess*` types.

---

## 5. Risks

| Risk | Handling |
|---|---|
| Required dashboard database raises setup cost | One-call registration, SQLite supported, documented quickstart. Chosen deliberately; revisit only if it blocks adoption. |
| Deployment identity model varies, so the resource prefix may not map to a service | Prefix is explicit configuration; service and instance live in routing keys, not in the prefix. Covered by a test where two services share one identity. |
| Broker read permissions are broad, so payloads are visible to other identities | Documented as a deployment consideration; the payloads authorization policy is separate so operators can withhold payload access at the dashboard even where the broker cannot. |
| Broker write permissions are broad, so any identity can inject commands | Agents authenticate callers themselves (section 2.10): `user_id` allowlist by default, HMAC for shared identities, dedicated vhost recommended where possible. No insecure default; startup fails if unconfigured. |
| Agents and dashboards can boot in any order, and neither can declare the other's exchange | Missing-exchange 404s are an expected state: isolated channel, catch, capped backoff, one warning. Covered by a Phase 4 test that starts the agent first. |
| Shared management secret (option 2) needs distribution and rotation | Only required where identities are shared. `user_id` allowlisting is the default and needs no secret. Rotation support is a documented option, not a startup requirement. |
| Migrations for the dashboard context could collide with an application database | Own configurable schema (`ratatoskr_dashboard`), shipped migrations rather than `EnsureCreated`, and a Phase 5 test that migrates into a database that already holds application tables. |
| Bounded-only bulk is slower for very large incidents | `*Matching` is server-batched and resumable by re-running with the same filter; `{remaining, capped}` tells the operator to repeat. Async jobs remain a clean minor-version addition. |
| Two HTTP surfaces could drift again | They are generated from one route tree and one DTO set; a test asserts the operation list is identical on both. |
| Core still supports only one RabbitMQ connection for app messaging | Out of scope by decision. The management side is already multi-connection, so the migration scenario is covered for the dashboard today and for messaging when core named transports land. |

---

## 6. Definition of done

**Architecture**

- `Ratatoskr.UI` and `Ratatoskr.Management` have no broker dependency.
- Exactly one implementation of each inbox/outbox operation, reached by the per-service REST API,
  the in-process transport, and the RabbitMQ transport.
- Multiple transports, including two of the same kind, are registered and used simultaneously.
- A second transport can be added without changing shared interfaces, proven by the fake transport
  and the conformance suite.

**Reliability**

- The full flow passes against RabbitMQ with the required least-privilege permissions.
- Two dashboard replicas and two service replicas behave as documented for discovery, replies, and
  logical vs instance targeting.
- Duplicate delivery mutates once; a lost response is safe to retry. A multi-batch matching
  operation interrupted mid-run resumes instead of restarting, and reports an accurate count.
- Boot order does not matter: an agent that starts before its dashboard, or a dashboard that starts
  before any agent, converges without manual intervention.
- Connection loss and topology recovery are tested; pending requests fail deterministically rather
  than waiting out their deadline.
- Unreachable instance targets fail fast rather than timing out.
- Nothing grows without bound: idempotency records, audit entries, correlation tables, SSE
  channels, and broker queues all have a bound or a retention policy.
- No scoped dependency is captured by a singleton; scope validation is on in the test host.
- Startup validation fails clearly on misconfiguration; shutdown completes with no abandoned work.

**Security**

- Hostile identifiers and payloads cannot execute script.
- Metadata, payload, requeue, delete, and bulk capabilities are independently authorizable.
- An agent rejects a command from an unauthenticated or non-allowlisted broker caller before
  dispatch, proven with a third-party identity under the real permission patterns.
- Cookie-authenticated mutations carry antiforgery protection; explicit-credential callers are not
  broken by it. Every mutation produces an audit record.
- No raw exception text crosses the protocol or reaches ProblemDetails.

**Documentation**

- Package topology, transport registration, least-privilege broker setup, control-plane caller
  authentication, protocol and delivery guarantees, idempotency and resume semantics, targeting,
  pagination, search cost, bulk semantics, dashboard migrations, and audit and retention behaviour
  are all documented and match the implementation.
