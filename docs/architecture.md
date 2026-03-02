# Ratatoskr Architecture Guide

Ratatoskr is a lightweight CloudEvents bus implementation for .NET. It provides transport-agnostic message publishing and consumption with optional durability guarantees through the EF Core Outbox and Inbox patterns.

## Package Overview

```mermaid
graph LR
    subgraph Core ["Ratatoskr (Core)"]
        IRatatoskr["IRatatoskr"]
        IMessageSender["IMessageSender"]
        IMessageHandler["IMessageHandler&lt;T&gt;"]
        MessageRouter["MessageRouter"]
        MessageDispatcher["MessageDispatcher"]
        HandlerInvoker["HandlerInvoker"]
        ChannelRegistry["ChannelRegistry"]
        LocalSender["LocalMessageSender"]
        LocalConsumer["LocalTransportConsumer"]
    end

    subgraph EfCore ["Ratatoskr.EfCore"]
        OutboxProcessor["OutboxProcessor"]
        InboxProcessor["InboxProcessor"]
        InboxAcceptor["InboxAcceptor"]
        DurableLocalSender["DurableLocalMessageSender"]
        OutboxInterceptor["OutboxTriggerInterceptor"]
    end

    subgraph RabbitMq ["Ratatoskr.RabbitMq"]
        RmqSender["RabbitMqMessageSender"]
        RmqConsumer["RabbitMqConsumer"]
        TopologyManager["RabbitMqTopologyManager"]
    end

    IRatatoskr --> IMessageSender
    IMessageSender -.-> LocalSender
    IMessageSender -.-> RmqSender
    IMessageSender -.-> DurableLocalSender
    DurableLocalSender --> LocalSender
    DurableLocalSender --> InboxAcceptor
    LocalConsumer --> MessageRouter
    RmqConsumer --> MessageRouter
    MessageRouter --> InboxAcceptor
    MessageRouter --> MessageDispatcher
    MessageDispatcher --> HandlerInvoker
    InboxProcessor --> HandlerInvoker
    HandlerInvoker --> IMessageHandler
```

The core library provides the abstractions (`IRatatoskr`, `IMessageSender`, `IMessageHandler<T>`, `MessageDispatcher`) and a built-in local (in-process) transport. The EfCore package adds outbox/inbox durability. The RabbitMq package provides a RabbitMQ transport.

---

## End-to-End Flow

The following diagram shows the complete message lifecycle — from publishing through transport to consumption and handler invocation. The sections below detail each step.

```mermaid
flowchart TD
    subgraph Publish ["Publishing"]
        App["Application"]
        Direct["IRatatoskr.PublishDirectAsync\nEnrich, serialize, send to matching transports"]
        Outbox["DbContext.OutboxMessages.Add\n+ SaveChangesAsync"]

        App --> Direct
        App --> Outbox
    end

    subgraph OutboxPipeline ["Outbox Pipeline"]
        Interceptor["OutboxTriggerInterceptor\nEnrich, serialize, persist in same DB transaction"]
        OutboxDB[("Database\nOutboxMessageEntity")]
        OutboxProc["OutboxProcessor\nBackground service, distributed lock"]

        Outbox --> Interceptor
        Interceptor --> OutboxDB
        OutboxDB --> OutboxProc
    end

    subgraph Transport ["Transport Layer"]
        SenderInterface["IMessageSender\nRoutes by TransportName"]
        Local["LocalMessageSender\nIn-memory channel"]
        Durable["DurableLocalMessageSender\nPersist to inbox DB, then channel"]
        RmqSend["RabbitMqMessageSender\nAMQP publish"]

        Direct --> SenderInterface
        OutboxProc --> SenderInterface
        SenderInterface -.-> Local
        SenderInterface -.-> Durable
        SenderInterface -.-> RmqSend
    end

    subgraph Consume ["Consumption"]
        InMemCh[/"In-Memory Channel"/]
        RmqQueue[/"RabbitMQ Queue"/]
        LocalConsumer["LocalTransportConsumer\nBackgroundService"]
        RmqConsumer["RabbitMqConsumer\nBackgroundService"]

        Local --> InMemCh
        Durable --> InMemCh
        RmqSend --> RmqQueue
        InMemCh --> LocalConsumer
        RmqQueue --> RmqConsumer
    end

    subgraph Dispatch ["Message Dispatch"]
        Router["MessageRouter\nAccept inbox handlers,\nthen dispatch"]
        Dispatcher["MessageDispatcher\nResolve type, deserialize,\nskip inbox handlers"]

        LocalConsumer --> Router
        RmqConsumer --> Router
        Router --> Dispatcher
    end

    subgraph Inbox ["Inbox Processing"]
        InboxAccept["InboxAcceptor\nPersist message + handler\nstatuses to DB"]
        InboxDB[("Database\nInboxMessageEntity\nInboxHandlerStatusEntity")]
        InboxProc["InboxProcessor\nBackground service, distributed lock"]

        Durable --> InboxAccept
        Router --> InboxAccept
        InboxAccept --> InboxDB
        InboxDB --> InboxProc
    end

    Invoker["HandlerInvoker\nResolve handler in DI scope,\ninvoke via compiled delegate"]
    Handler["IMessageHandler‹T›"]
    Dispatcher --> Invoker
    InboxProc --> Invoker
    Invoker --> Handler
```

---

## Publishing

There are two ways to publish messages: directly via `IRatatoskr`, or transactionally via the EF Core Outbox.

### Direct Publishing

```mermaid
sequenceDiagram
    participant App as Application
    participant R as IRatatoskr
    participant E as MessagePropertiesEnricher
    participant S as IMessageSerializer
    participant Sender as IMessageSender[]

    App->>R: PublishDirectAsync<TMessage>(message)
    R->>E: Enrich(props)
    Note over E: Add ID, timestamp, trace context,<br/>resolve target transports
    R->>S: Serialize(message) → byte[]
    loop For each matching transport
        R->>Sender: SendAsync(bytes, props)
    end
```

The application calls `IRatatoskr.PublishDirectAsync<T>()`. Ratatoskr enriches the message properties (CloudEvents ID, timestamp, trace context), serializes the message, then sends it to all `IMessageSender` implementations matching the configured transports.

### Transactional Publishing (EF Core Outbox)

```mermaid
sequenceDiagram
    participant App as Application
    participant Db as DbContext
    participant Int as OutboxTriggerInterceptor
    participant DB as Database
    participant OP as OutboxProcessor
    participant Sender as IMessageSender

    App->>Db: OutboxMessages.Add(message)
    App->>Db: SaveChangesAsync()
    activate Int
    Int->>Int: Enrich, serialize & create OutboxMessageEntity
    Int->>DB: Save business data + outbox entries (same transaction)
    Int->>OP: TriggerAsync()
    deactivate Int
    OP->>DB: Query pending outbox messages
    loop For each pending message
        OP->>Sender: SendAsync(content, props)
        OP->>DB: Mark as processed
    end
```

Messages are added to the `DbContext` and persisted in the same database transaction as business data. The `OutboxTriggerInterceptor` hooks into EF Core's `SaveChangesAsync` to create `OutboxMessageEntity` records and signal the `OutboxProcessor`. The processor runs as a background service, acquires a distributed lock, and dispatches pending messages to the appropriate transport.

On failure, the outbox retries with exponential backoff. After exceeding the maximum retry count, the message is marked as poisoned.

---

## Consuming & Dispatch

All transports follow the same consumption path: messages are routed through the `MessageRouter` (which handles inbox acceptance) and then through the `MessageDispatcher`, which resolves handlers and invokes them.

### RabbitMQ Transport

```mermaid
sequenceDiagram
    participant Q as RabbitMQ Queue
    participant C as RabbitMqConsumer
    participant M as EnvelopeMapper
    participant R as MessageRouter
    participant D as MessageDispatcher
    participant H as IMessageHandler

    Q->>C: Message delivered
    C->>M: MapIncoming(amqpProps, body)
    M-->>C: MessageProperties + body
    C->>R: RouteAsync(body, props)
    Note over R: Accept inbox handlers (if configured),<br/>then dispatch
    R->>D: DispatchAsync(body, props)
    D->>H: HandleAsync (non-inbox handlers only)
    R-->>C: DispatchResult
    alt Success
        C->>Q: BasicAckAsync
    else Error
        C->>C: RabbitMqRetryHandler
        alt Recoverable & retries remaining
            C->>Q: Nack / requeue with delay
        else Permanent or max retries
            C->>Q: Route to Dead Letter Queue
        end
    end
```

On startup, `RabbitMqTopologyManager` provisions exchanges, queues, and bindings. The `RabbitMqConsumer` background service subscribes to configured queues. When a message arrives, the CloudEvents AMQP mapper extracts `MessageProperties` and the body from AMQP headers, then passes them to the `MessageRouter`.

The `MessageRouter` handles inbox acceptance (persisting inbox-managed handler statuses to the database if configured) and delegates to `MessageDispatcher` for non-inbox handler invocation. The consumer has no inbox awareness — it just calls `RouteAsync` and acts on the result. On errors, `RabbitMqRetryHandler` either requeues with a delay or routes to a Dead Letter Queue.

### Local Transport

```mermaid
sequenceDiagram
    participant S as LocalMessageSender
    participant Ch as In-Memory Channel
    participant C as LocalTransportConsumer
    participant R as MessageRouter
    participant D as MessageDispatcher
    participant H as IMessageHandler

    S->>Ch: WriteAsync(message)
    Ch->>C: ReadAsync (BackgroundService)
    C->>R: RouteAsync(body, props)
    Note over R: Accept inbox handlers (if configured),<br/>then dispatch
    R->>D: DispatchAsync(body, props)
    D->>H: HandleAsync (non-inbox handlers only)
```

The local transport uses an in-memory `System.Threading.Channels.Channel<T>`. `LocalMessageSender` writes to the channel, and `LocalTransportConsumer` (a `BackgroundService`) reads from it and routes messages through the `MessageRouter` — the same pipeline used by the RabbitMQ consumer. The router handles inbox acceptance (if configured) before delegating to `MessageDispatcher` for non-inbox handler invocation.

> **Note:** When `UseEfCoreInbox` and `UseLocalTransport` are both configured, the local sender is automatically replaced with `DurableLocalMessageSender`, which writes inbox entries to the database **before** writing to the in-memory channel. This means local publishes will have database-level latency and require database availability. See the [Inbox — Crash safety](inbox.md#crash-safety-local-transport) section for details.

### Message Dispatch

```mermaid
flowchart TD
    D[MessageDispatcher.DispatchAsync] --> Resolve[Resolve message CLR type<br/>from ChannelRegistry]
    Resolve --> Deserialize[Deserialize body to message object]
    Deserialize --> FindHandlers[Find all registered<br/>IMessageHandler&lt;T&gt; implementations]
    FindHandlers --> SkipInbox[Skip inbox-managed handlers<br/>via InboxHandlerRegistry]
    SkipInbox --> InvokeAll[Invoke remaining handlers<br/>via HandlerInvoker]
    InvokeAll --> ReturnResult[Return DispatchResult]
```

The `MessageDispatcher` resolves the message type from the `ChannelRegistry`, deserializes it, then delegates to `HandlerInvoker` for each non-inbox handler. Inbox-managed handlers are skipped — they have already been persisted to the database by the `InboxAcceptor` (called by the `MessageRouter` before dispatch) and will be delivered later by the `InboxProcessor`, which also uses `HandlerInvoker`.

If all handlers are inbox-managed, the dispatcher returns `NoHandlers`. The `MessageRouter` combines this with the `InboxAcceptor` result to determine the final outcome (treating it as success when inbox handlers were persisted).

---

## Inbox Pattern

The inbox provides per-handler durability, deduplication, and isolated retry. Each handler registered with a stable key gets its own database entry and is processed independently.

### Registration

Handlers opt into the inbox by providing a stable key:

```csharp
builder.AddHandler<OrderCreated, OrderCreatedHandler>(h =>
    h.WithInbox("order-created-handler"));
```

Without a key, the handler is fire-and-forget — invoked inline by the dispatcher without durability guarantees.

### Inbox Processing

```mermaid
sequenceDiagram
    participant IP as InboxProcessor
    participant DB as Database
    participant DI as DI Container
    participant H as IMessageHandler

    loop Polling / triggered
        IP->>DB: Acquire distributed lock
        IP->>DB: Query pending InboxHandlerStatusEntity<br/>(not completed, not poisoned, due for retry)
        IP->>DB: Mark as processing (Version++)
        loop For each handler status
            IP->>DB: Load InboxMessageEntity
            IP->>DI: Resolve handler by key
            IP->>H: HandleAsync(message, props)
            alt Success
                IP->>DB: MarkAsCompleted (CompletedAt = now)
            else Failure
                IP->>DB: MarkAsFailed (ErrorCount++, NextAttemptAt = backoff)
                Note over DB: If ErrorCount >= MaxRetries → IsPoisoned = true
            end
            IP->>DB: SaveChangesAsync (per handler)
        end
    end
```

The `InboxProcessor` runs as a background service with a distributed lock. It queries pending `InboxHandlerStatusEntity` records, claims them via optimistic concurrency (Version increment), and invokes each handler via the shared `HandlerInvoker` (same component used by `MessageDispatcher`). Progress is saved per handler — a failure in one handler does not affect others.

### Durable Local Transport

When both the local transport and inbox are enabled, `UseEfCoreInbox` automatically replaces the `LocalMessageSender` with `DurableLocalMessageSender`:

```mermaid
sequenceDiagram
    participant App as Application
    participant DLS as DurableLocalMessageSender
    participant DB as Database
    participant Ch as In-Memory Channel
    participant IP as InboxProcessor
    participant H as IMessageHandler

    App->>DLS: SendAsync(bytes, props)
    DLS->>DB: InboxAcceptor.AcceptAsync()
    Note over DLS,DB: Inbox handlers written to DB first
    DLS->>Ch: WriteAsync(message)
    Note over DLS,Ch: Non-inbox handlers via channel
    Note over DB,IP: InboxProcessor picks up<br/>persisted handlers on next poll
    IP->>H: HandleAsync(message, props)
```

The durable sender delegates to `InboxAcceptor` to write inbox-managed handler statuses to the database **before** writing to the in-memory channel. This ensures crash safety: if the process dies after the DB write but before the channel write, the `InboxProcessor` still picks up the handlers on restart.

### Retry & Backoff

Both outbox and inbox use exponential backoff for failed messages:

```
NextAttemptAt = now + min(2^ErrorCount seconds, MaxRetryDelay)

Example with MaxRetryDelay = 5 min:
  Attempt 1 → retry after 2s
  Attempt 2 → retry after 4s
  Attempt 3 → retry after 8s
  Attempt 4 → retry after 16s
  ...
  Attempt 9+ → capped at 300s (5 min)
```

After exceeding the maximum retry count, the entry is marked as **poisoned** and no longer processed.

### Stuck Message Detection

If a handler has been in "processing" state longer than the configured threshold (default: 5 minutes), the `InboxProcessor` clears its `ProcessingStartedAt` field, making it eligible for retry. This handles cases where a worker crashes mid-processing.

---

## Database Schema

### Outbox

**`OutboxMessageEntity`** — one row per message per transport.

| Column | Description |
|---|---|
| `Id` | Primary key (GUID v7) |
| `Content` | Serialized message body |
| `SerializedProperties` | JSON-encoded MessageProperties |
| `TransportName` | Target transport |
| `CreatedAt` | When the message was staged |
| `ProcessedAt` | When successfully sent (null while pending) |
| `ProcessingStartedAt` | Set during processing, cleared on completion |
| `ErrorCount` | Number of failed attempts |
| `Error` | Last error message |
| `NextAttemptAt` | Exponential backoff timestamp |
| `IsPoisoned` | True after max retries exceeded |

### Inbox

**`InboxMessageEntity`** — one row per unique message (keyed by CloudEvents ID).

| Column | Description |
|---|---|
| `Id` | CloudEvents message ID (string, PK) |
| `TransportName` | Source transport |
| `Content` | Serialized message body |
| `SerializedProperties` | JSON-encoded MessageProperties |
| `ReceivedAt` | When the message was first received |

**`InboxHandlerStatusEntity`** — one row per handler per message.

| Column | Description |
|---|---|
| `Id` | Primary key (GUID v7) |
| `MessageId` | FK → InboxMessageEntity.Id |
| `HandlerKey` | Stable handler key (unique with MessageId) |
| `ErrorCount` | Number of failed attempts |
| `LastError` | Last error message |
| `ProcessingStartedAt` | Set during processing, cleared on completion |
| `NextAttemptAt` | Exponential backoff timestamp |
| `IsPoisoned` | True after max retries exceeded |
| `CompletedAt` | When successfully handled (null while pending) |
| `Version` | Optimistic concurrency token |

The unique constraint on `(MessageId, HandlerKey)` provides deduplication — concurrent delivery of the same message safely resolves via constraint violation.

---

## Concurrency & Distribution

Ratatoskr is designed for multi-instance deployment:

- **Distributed locks** (via Medallion.Threading) — Both `OutboxProcessor` and `InboxProcessor` acquire a named lock before processing. Only one instance processes at a time.
- **Optimistic concurrency** — `InboxHandlerStatusEntity.Version` prevents two workers from processing the same handler status simultaneously.
- **Idempotent persistence** — The inbox acceptor uses unique constraints for deduplication. Concurrent inserts safely resolve via constraint violations.

---

## Observability

Ratatoskr integrates with OpenTelemetry via `System.Diagnostics.Activity`:

- **Publishing** — Injects W3C trace context (`TraceParent`, `TraceState`) into message properties and transport headers.
- **Consuming** — Extracts trace context to continue the distributed trace.
- **Metrics** — Records receive lag, process duration, and message counts, tagged with messaging semantic conventions (`messaging.system`, `messaging.destination.name`).

### Message Activity Observers

`IMessageActivityObserver` implementations are notified at various pipeline stages (`Published`, `Received`, `Dispatched`, `OutboxStaged`, `OutboxSent`, `InboxQueued`, `InboxDispatched`, `InboxPoisoned`). Observers are designed for **testing and instrumentation** — they are not a mechanism for reliable side effects:

- Observer exceptions are always caught and logged at `Warning` level. They never affect the message pipeline.
- If an observer throws, the message is still processed normally.

Use `Ratatoskr.Testing`'s `MessageTrackingSession` (which is backed by an observer) for asserting message flow in integration tests.

---

## Message Schema Evolution

Ratatoskr uses `System.Text.Json` for message serialization. By default:

- New fields added to a message type will deserialize as `default` for in-flight messages that don't contain them.
- Removed fields in new code will be silently ignored during deserialization of old messages.
- Renamed fields will appear as new fields (old data lost).

**Recommendations:**

- Only add fields (additive changes). Never rename or remove fields that may exist in in-flight outbox/inbox messages.
- For breaking changes, introduce a new message type and migrate consumers before producers.
- Consider using a `SchemaVersion` CloudEvents extension attribute for future compatibility checks.
