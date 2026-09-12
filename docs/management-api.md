# Management HTTP API Reference

In addition to the web dashboard, any service hosting `Ratatoskr.Management.EfCore` can expose a direct, public REST API for inspection and operations. This allows command-line tools, external monitoring systems, deployment scripts, or custom administration tools to interact with Ratatoskr directly without going through the web UI or RabbitMQ broker.

---

## Endpoint Registration

Install `Ratatoskr.Management.EfCore` in the service and map the endpoints:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register agent and EF Core operations
builder.Services.AddRatatoskrManagementAgent(agent =>
{
    agent.ServiceName = "orders-service";
    agent.InstanceId = Environment.MachineName;
});

// Configure authorization policies
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("OpsMetadata", policy => policy.RequireRole("Operator", "Administrator"))
    .AddPolicy("OpsPayloads", policy => policy.RequireRole("Operator", "Administrator"))
    .AddPolicy("OpsRequeue", policy => policy.RequireRole("Operator", "Administrator"))
    .AddPolicy("OpsDelete", policy => policy.RequireRole("Administrator"))
    .AddPolicy("OpsBulk", policy => policy.RequireRole("Administrator"));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Map the management REST API
app.MapRatatoskrManagementApi(
    new RatatoskrUiAuthorizationPolicies(
        Metadata: "OpsMetadata",
        Payloads: "OpsPayloads",
        Requeue: "OpsRequeue",
        Delete: "OpsDelete",
        Bulk: "OpsBulk"
    ),
    prefix: "/ratatoskr/api/v1"
);

app.Run();
```

---

## Common Headers

| Header | Description | Required |
|---|---|---|
| `X-Ratatoskr-Operation-Id` | Unique client-generated GUID idempotency key for mutations (`POST`). If a request is retried with the same operation ID, the server returns the cached result without repeating the mutation. | Optional (Recommended for mutations) |
| `Authorization` | Bearer token or cookie for ASP.NET Core authorization. Requests bearing `Authorization: Bearer` bypass antiforgery requirements. | Depending on policy |

---

## Pagination & Cursors

All list endpoints use keyset pagination ordered by `CreatedAt DESC`, then entity ID:

- `limit` (query integer, default `25`, max `100`): Maximum items to return.
- `cursor` (query string, optional): Opaque base64-encoded cursor token returned in `nextCursor` from the previous page.

Response shape:

```json
{
  "items": [ ... ],
  "nextCursor": "MjAyNi0wOS0xMlQxOTowMDowMFpfMDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAx",
  "hasMore": true
}
```

> [!NOTE]
> Keyset pagination does not return total counts or support page numbers. For count estimations, use the dedicated `POST .../count` endpoint.

---

## Service & Discovery Endpoints

### `GET /ratatoskr/api/v1/describe`
Returns the service announcement manifest, including capabilities, registered channels, and transports.

**Policy**: `Metadata`

**Example Response**:
```json
{
  "serviceName": "orders-service",
  "instanceId": "orders-pod-1",
  "protocolVersion": "1.0",
  "capabilities": [
    { "name": "outbox", "version": "1.0" },
    { "name": "inbox", "version": "1.0" }
  ],
  "contexts": ["OrderDbContext"]
}
```

### `GET /ratatoskr/api/v1/contexts`
Returns the names of all registered `DbContext` resources in this service.

**Policy**: `Metadata`

**Example Response**:
```json
["OrderDbContext", "BillingDbContext"]
```

### `GET /ratatoskr/api/v1/health`
Checks whether management handlers and the underlying database context can be resolved.

**Policy**: `Metadata`

---

## Outbox Endpoints

All outbox endpoints accept an optional query parameter `context` when multiple `DbContext` instances are registered.

### `GET /ratatoskr/api/v1/outbox`
Lists outbox messages with keyset pagination.

**Policy**: `Metadata`

**Query Parameters**:
- `status`: Filter by state (`"pending"`, `"processing"`, `"published"`, `"failed"`, `"poisoned"`).
- `from`: ISO 8601 timestamp (e.g. `2026-09-01T00:00:00Z`).
- `to`: ISO 8601 timestamp.
- `search`: Case-insensitive substring match on message type or error message.
- `limit`: Number of items (default 25).
- `cursor`: Keyset token from `nextCursor`.

### `GET /ratatoskr/api/v1/outbox/{id}`
Returns details for a single outbox message, including error details and payload.

**Policy**: `Payloads`

### `POST /ratatoskr/api/v1/outbox/requeue`
Retries specific outbox messages by resetting their retry counter and scheduling immediate delivery.

**Policy**: `Requeue`

**Request Body**:
```json
{
  "ids": ["0191ebc5-685a-733f-846f-5fb0268579d4"]
}
```

**Response**:
```json
{
  "requeued": 1,
  "skipped": 0
}
```

### `POST /ratatoskr/api/v1/outbox/delete`
Permanently deletes specific outbox messages.

**Policy**: `Delete`

**Request Body**:
```json
{
  "ids": ["0191ebc5-685a-733f-846f-5fb0268579d4"]
}
```

### `POST /ratatoskr/api/v1/outbox/count`
Returns the exact count of outbox messages matching the specified filter without making any mutations.

**Policy**: `Metadata`

**Request Body**:
```json
{
  "status": "poisoned",
  "from": "2026-09-10T00:00:00Z",
  "search": "TimeoutException"
}
```

**Response**:
```json
{
  "count": 42
}
```

### `POST /ratatoskr/api/v1/outbox/preview`
Returns a sample of messages matching the filter before executing a bulk mutation.

**Policy**: `Metadata`

### `POST /ratatoskr/api/v1/outbox/requeue/matching`
Performs a bounded bulk requeue of outbox messages matching criteria.

**Policy**: `Bulk`

**Request Body**:
```json
{
  "status": "poisoned",
  "search": "TimeoutException"
}
```

> [!IMPORTANT]
> Bulk matching requests require at least one filter criterion (`status`, `from`, `to`, or `search`). Unbounded requests without filters are rejected with HTTP 400.

**Response**:
```json
{
  "processed": 42,
  "remaining": 0,
  "capped": false
}
```

### `POST /ratatoskr/api/v1/outbox/delete/matching`
Performs a bounded bulk deletion of outbox messages matching criteria.

**Policy**: `Bulk`

---

## Inbox Endpoints

Inbox operations target handler statuses associated with received messages.

### `GET /ratatoskr/api/v1/inbox`
Lists inbox handler statuses with keyset pagination.

**Policy**: `Metadata`

**Query Parameters**:
- `status`: Filter by state (`"pending"`, `"processing"`, `"completed"`, `"failed"`, `"poisoned"`).
- `handlerKey`: Filter by specific handler registration key.
- `from`: ISO 8601 start timestamp.
- `to`: ISO 8601 end timestamp.
- `search`: Substring match on message type or error message.
- `limit`: Number of items (default 25).
- `cursor`: Keyset token from `nextCursor`.

### `GET /ratatoskr/api/v1/inbox/{id}`
Returns details for a single inbox message and its handler status.

**Policy**: `Payloads`

### `POST /ratatoskr/api/v1/inbox/requeue`
Retries specific inbox handler statuses.

**Policy**: `Requeue`

### `POST /ratatoskr/api/v1/inbox/delete`
Permanently deletes specific inbox handler statuses and removes orphaned messages.

**Policy**: `Delete`

### `POST /ratatoskr/api/v1/inbox/count`
Counts inbox handler statuses matching filter criteria.

**Policy**: `Metadata`

### `POST /ratatoskr/api/v1/inbox/preview`
Previews inbox items matching filter criteria.

**Policy**: `Metadata`

### `POST /ratatoskr/api/v1/inbox/requeue/matching`
Performs bounded bulk retry of inbox handler statuses.

**Policy**: `Bulk`

### `POST /ratatoskr/api/v1/inbox/delete/matching`
Performs bounded bulk deletion of inbox handler statuses.

**Policy**: `Bulk`

---

## Error Handling & ProblemDetails

All errors follow the RFC 7807 `ProblemDetails` specification:

```json
{
  "type": "https://ratatoskr.dev/errors/target_unreachable",
  "title": "Target Unreachable",
  "status": 404,
  "detail": "The announcement for 'billing-service' carries no RabbitMQ address.",
  "extensions": {
    "code": "target_unreachable",
    "isRetryable": false
  }
}
```

### Protocol Error Codes

| Code | HTTP Status | Meaning | Retryable |
|---|---|---|---|
| `target_unreachable` | 404 | Target service or instance queue is not registered or not listening. | No |
| `unsupported_operation` | 400 | Operation is not supported by the target service. | No |
| `invalid_argument` | 400 | Missing mandatory parameters or invalid cursor. | No |
| `conflict` | 409 | Operation ID was already applied with different parameters. | No |
| `deadline_exceeded` | 504 | Target service did not answer before the deadline elapsed. | Yes |
| `transport_unavailable` | 503 | Underlying transport or broker connection dropped. | Yes |
| `internal_error` | 500 | Unhandled exception occurred during execution. | No |
