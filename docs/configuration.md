# Configuration

**Channel-First, Intent-Based Fluent API** that:

1.  **Centralizes Topology**: You define a "Channel" (Exchange) once, applying transport settings (Type, Durability) to the Channel.
2.  **Groups Messages**: You attach multiple Message Types to a Channel.
3.  **Enforces Ownership**: Distinct methods for `EventPublish`, `CommandPublish`, `CommandConsume`, `EventConsume` enforce who declares vs who validates.
4.  **Fails Fast**: Validates topological dependencies on startup.

## Ownership Rules

| Intent | API Method | Ownership | Action (RabbitMQ) |
| :--- | :--- | :--- | :--- |
| **Produces Event** | `AddEventPublishChannel` | **Us** (Originator) | **Declare** Exchange (Topic) |
| **Sends Command** | `AddCommandPublishChannel` | **Them** (Receiver) | **Validate** Exchange Exists |
| **Consumes Command** | `AddCommandConsumeChannel` | **Us** (Processor) | **Declare** Exchange (Direct) + Queue |
| **Consumes Event** | `AddEventConsumeChannel` | **Them** (Publisher) | **Validate** Exchange + **Declare** Queue + **Bind** |

## API Design: Channel-First

Everything happens inside `AddRatatoskr`.

### Configuration Example

```csharp
services.AddRatatoskr(builder => 
{
    builder.WithServiceName("OrderService");
    
    // Transport Configuration (Global)
    builder.UseRabbitMq(mq => mq.ConnectionString("amqp://..."));

    // Handlers (Generic Registration)
    // Registers THandler as IMessageHandler<TMessage> in DI
    builder.AddHandler<CreateOrderHandler>();
    builder.AddHandler<UserRegisteredHandler>();

    // ==========================================
    // 1. PRODUCER: Events we own
    // Intent: We govern "orders.events". We declare it.
    // ==========================================
    builder.AddEventPublishChannel("orders.events")
           .WithRabbitMq(cfg => cfg
               .WithTopicExchange()
           )
           // Default Routing Key: Uses [RatatoskrMessage("type")] or typeof(T).Name
           .Produces<OrderCreated>() 
           // Overridden Routing Key
           .Produces<OrderCancelled>(cfg => cfg.WithRoutingKey("order.cancelled"));

    // ==========================================
    // 2. SENDER: Commands to others
    // Intent: We send to "payments.commands". We expect it to exist.
    // ==========================================
    builder.AddCommandPublishChannel("payments.commands")
           // We validate it exists and matches expectations (e.g. is Topic/Direct as expected)
           .WithRabbitMq(cfg => cfg
               .WithDirectExchange()
           )
           .Produces<ProcessPayment>(cfg => cfg.WithRoutingKey("cmd.pay"));

    // ==========================================
    // 3. CONSUMER: Commands we own
    // Intent: We own "orders.commands". We declare it and our queue.
    // ==========================================
    builder.AddCommandConsumeChannel("orders.commands")
           .WithRabbitMq(cfg => cfg
                .WithDirectExchange()
                // For Command Consumption, we usually bind a specific queue
                .WithQueueName("orders.process")
           )
           // Implicitly binds using Routing Key derived from Type or Attribute
           .Consumes<CreateOrder>(); 

    // ==========================================
    // 4. CONSUMER: Events from others
    // Intent: We listen to "users.events". We expect it to exist. We declare our queue.
    // ==========================================
    builder.AddEventConsumeChannel("users.events")
           .WithRabbitMq(cfg => cfg
                .WithQueueName("orders.user-handler") // The shared queue for this channel's subscriptions
                .WithTopicExchange() // We expect this type
                // Optional: Global settings for this consumer (e.g. Prefetch)
            )
           // Binds queue "orders.user-handler" to exchange "users.events" with key "user.registered"
           .Consumes<UserRegistered>(cfg => cfg.WithType("user.registered")); 
});
```

### Attributes

Instead of specifying the type via `WithType`, you can also use `[RatatoskrMessage("type")]` to identify messages.

```csharp
[RatatoskrMessage("order.created")]
public record OrderCreated(string OrderId);
```

## Registry Architecture

### 1. ChannelRegistry
The root container that holds all the channel information, which in turn hold all the message informations. This is populated from the `AddRatatoskr` configuration.
