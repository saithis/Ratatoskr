# Management UI and control plane

Ratatoskr management is optional and transport-neutral. `Ratatoskr.UI` depends only on
`IManagementClient` and `IServiceCatalog`; it does not reference RabbitMQ or the application's
message transport. Providers own their topology and registration, so a second provider can be
added without changing the dashboard, shared protocol contracts, or operation handlers.

## Package topology

- `Ratatoskr.Management.Abstractions` contains dependency-free versioned contracts, targets,
  discovery, topology, and provider interfaces.
- `Ratatoskr.Management` contains the agent, EF Core operations, dispatcher, and in-process
  provider.
- `Ratatoskr.Management.RabbitMq` contains the RabbitMQ command, reply, and discovery provider.
- `Ratatoskr.UI` contains the embedded dashboard, REST facade, SSE, and authorization.

## In-process registration

Use the default provider for a modular monolith, local development, or a co-hosted dashboard.

```csharp
builder.Services.AddRatatoskrManagement(options =>
{
    options.ServiceName = "orders";
    options.InstanceId = Environment.MachineName;
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("RatatoskrMetadata", policy => policy.RequireRole("Operator"))
    .AddPolicy("RatatoskrPayloads", policy => policy.RequireRole("Operator"))
    .AddPolicy("RatatoskrRequeue", policy => policy.RequireRole("Operator"))
    .AddPolicy("RatatoskrDelete", policy => policy.RequireRole("Administrator"))
    .AddPolicy("RatatoskrBulk", policy => policy.RequireRole("Administrator"));

builder.Services.AddRatatoskrUI();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapRatatoskrUI(new RatatoskrUiAuthorizationPolicies(
    "RatatoskrMetadata", "RatatoskrPayloads", "RatatoskrRequeue", "RatatoskrDelete", "RatatoskrBulk"));
```

## RabbitMQ provider registration

Provider selection is independent of `bus.UseRabbitMq`. Install
`Ratatoskr.Management.RabbitMq` in every dashboard and managed-service process, then register
it after `AddRatatoskrManagement`:

```csharp
builder.Services.AddRabbitMqManagement(options =>
{
    options.ExchangePrefix = "operations";
    options.RequestTimeout = TimeSpan.FromSeconds(10);
});
```

Each dashboard instance has an ephemeral reply queue, so replicas do not consume one another's
replies. Discovery is broadcast to each dashboard replica. The provider declares bounded,
expiring queues and uses publisher confirms for replies whose loss would make a mutation
ambiguous.

## Protocol, delivery, and targeting

Requests, responses, and discovery announcements carry a protocol major/minor version. A
receiver accepts the same major version and a minor version no newer than it supports.
Unsupported versions and operations return stable protocol error codes.

Commands are delivered at least once. Mutations carry an operation ID; retry a lost response
with the same ID, and the receiver must apply the mutation only once logically. A logical-service
target reaches one eligible replica per delivery attempt. Adding `InstanceId` targets exactly that
replica, while `ResourceId` identifies a provider-neutral resource such as a DbContext.

## Pagination and topology

The dashboard list APIs use keyset pagination. Request `limit` and, for subsequent pages, supply
the opaque `cursor` returned as `nextCursor`. Do not use page numbers or derive cursors. Results
are ordered by newest `CreatedAt`, then resource ID.

Topology is exposed as logical channels with provider-owned transport bindings. RabbitMQ can
contribute exchange, queue, and routing-key properties; another provider can contribute topics,
partitions, and consumer groups without changing the UI API.

## Authorization and auditing

`MapRatatoskrUI` requires policies. Use separate policies for metadata, payloads, requeueing,
deletion, and bulk operations; the single-policy overload is for deployments that intentionally
grant all capabilities together. Protect cookie-authenticated mutations with the application's
antiforgery strategy. Audit mutations with the actor, operation ID, target, filter or selected
IDs, start/completion timestamps, and outcome. Bulk actions must be explicitly filtered or
selected and bounded by batch and total-operation limits.
