# Ratatoskr.Management.EfCore

The single implementation of every Ratatoskr inbox and outbox management operation, plus the
per-service management REST API.

```csharp
services.AddRatatoskrManagementEfCore(agent =>
{
    agent.ServiceName = "orders";
    agent.InstanceId  = Environment.MachineName;
});
services.AddRatatoskrManagementOperationCleanup<OrdersDbContext>();

app.MapRatatoskrManagementApi("RatatoskrAdmin");
```

## What you get

- `outbox.*` and `inbox.*` operations: keyset-paginated lists, a count that backs the
  destructive-action preview, payload detail, explicit-id requeue and delete, and bounded
  filter-driven variants.
- Persisted operation ids, written in the same transaction as the mutation, so an at-least-once
  control plane can retry safely. A duplicate delivery replays the recorded result.
- A retention worker for the operation log, registered per DbContext.

## Notes that matter in production

- **Lists carry no total.** A total is a second full scan of the filtered set. Ask `*/count` when
  you actually need one; the preview flow already does.
- **Search is a substring match** over serialized CloudEvents properties — a `LIKE` with a leading
  wildcard, not a full-text index. It must be paired with a time window or a status other than
  `all`, otherwise it is refused with `unbounded_search`.
- **Bulk operations are bounded.** A filter is mandatory, the run stops at `MaxTotalOperations` or
  at the request deadline, and the answer reports `{processed, remaining, capped}`.
- **`*.requeueMatching` applies only to poisoned rows.** Requeueing anything else would reset the
  error counters of rows a processor is still working on.
