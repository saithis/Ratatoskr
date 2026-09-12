# Ratatoskr.Management.RabbitMq

The RabbitMQ control-plane transport for Ratatoskr management. It has its own connection, its own
topology, and **no dependency on `Ratatoskr.RabbitMq`** — so a dashboard process needs no messaging
stack at all, and the control plane can live on a different broker, vhost or credential from the
one the application publishes on.

```csharp
// A managed service
services.AddRatatoskrManagementAgent(agent =>
{
    agent.ServiceName = "orders";
    agent.InstanceId  = Environment.MachineName;
    agent.AddRabbitMq("eu", o =>
    {
        o.ConnectionString    = new Uri(euConnectionString);
        o.DiscoveryExchange   = "dashboard.mgmt.discovery.inbox";
        o.AllowedCallers.Add("dashboard");
    });
});

// A dashboard
services.AddRatatoskrDashboard(dashboard =>
{
    dashboard.UseStore(db => db.UseNpgsql(dashboardConnectionString));
    dashboard.AddRabbitMq("eu", o => { o.ConnectionString = new Uri(euConnectionString); });
    dashboard.AddRabbitMq("us", o => { o.ConnectionString = new Uri(usConnectionString); });
});
```

## Topology

Everything here is permitted by the least-privilege permission patterns in `docs/rabbitmq.md`.

| Owner | Resource | Kind |
|---|---|---|
| Agent | `{prefix}.mgmt.cmd.inbox` | direct, durable — the command entry point |
| Agent | `{prefix}.mgmt.cmd.{service}.q` | durable queue, bound `svc.{service}` |
| Agent | `{prefix}.mgmt.cmd.{service}.{instance}.q` | exclusive queue, bound `inst.{instance}` |
| Dashboard | `{prefix}.mgmt.discovery.inbox` | fanout, durable — the heartbeat sink |
| Dashboard | `{prefix}.mgmt.discovery.{replica}.q` | exclusive queue |
| Dashboard | `{prefix}.mgmt.reply.inbox` | direct, durable — the reply sink |
| Dashboard | `{prefix}.mgmt.reply.{replica}.q` | exclusive queue, bound `{replica}` |

`ResourcePrefix` defaults to the connection user name and is explicit configuration; nothing
assumes it maps to one service, so several services may share one RabbitMQ identity.

## Caller authentication is required

Every identity in a RabbitMQ vhost may publish to any `*.inbox` exchange, so the broker is not an
authentication boundary. An agent refuses to start unless you choose one of:

1. **`AllowedCallers`** — the broker validates `user_id` against the authenticated connection, so a
   present `user_id` is a trustworthy sender identity at no cost. An absent one is untrusted.
2. **`SharedSecret`** — an HMAC over the envelope, the operation id and the deadline, for
   deployments where several parties authenticate as one RabbitMQ user.
3. **`AllowUnauthenticatedCallers`** — only on a vhost dedicated to management.

## Behaviour worth knowing

- Commands are published `mandatory`, so an instance-targeted command whose replica is gone fails
  immediately with `target_unreachable` instead of waiting out the deadline. A service queue with
  no live consumer is *not* returned: the command waits for a replica to come back.
- Heartbeats, replies and command consumption each own a channel. Publishing into an exchange the
  receiver has not declared yet closes the channel with `404`, which is a routine boot-ordering
  state — the publisher warns once, reopens, and retries with capped backoff.
- A dropped connection fails every pending request at once with `transport_unavailable`, because
  the exclusive reply queue went with it.
