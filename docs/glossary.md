# Key Terminology

| Term | Definition |
|------|------------|
| **Channel** | A named messaging endpoint that groups related message types. Channels have intent: event publish, event consume, command publish, or command consume. |
| **Transport** | The delivery mechanism that moves messages between services. Ratatoskr supports RabbitMQ and EF Core (database) transports. |
| **Handler** | A class implementing `IMessageHandler<T>` that processes a specific message type. Handlers are registered on consume channels. |
| **Handler key** | A stable string identifier for an inbox-managed handler (e.g., `"fulfillment"`). Persisted in the database — must not change across deployments. |
| **Fire-and-forget** | A handler registered without a key. Invoked immediately during message dispatch — no persistence, no retry. |
| **Inbox-managed** | A handler registered with a key on a channel with `UseInbox()`. Persisted to the database and delivered with retry, deduplication, and isolation. |
| **Poisoned message** | A message or handler status that has exhausted its retry budget. Remains in the database for manual investigation. |
| **Route interceptor** | An `IMessageRouteInterceptor` implementation called by the `MessageRouter` before dispatch. Used by the inbox to accept messages into the database. |
