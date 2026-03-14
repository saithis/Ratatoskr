---
title: "Inbox handler key required when UseInbox is configured on a channel"
category: documentation-issues
date: 2026-03-14
tags: [inbox, handler-key, example-project, docfx, build-time-validation]
module: Ratatoskr.EfCore
symptom: "Example project throws InvalidOperationException at startup"
root_cause: "Handler registered without key on inbox-enabled channel"
---

## Problem

The `examples/Docs/Program.cs` example project registered `OrderPlacedHandler` without a handler key on a channel that had `.UseInbox<OrderDbContext>()` enabled:

```csharp
// BROKEN — missing handler key
bus.AddEventConsumeChannel("orders.events", c => c
    .WithRabbitMq(r => r.WithQueueName("orders.events.subscriptions"))
    .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedHandler>())
    .UseInbox<OrderDbContext>());
```

Ratatoskr requires stable handler keys for inbox-managed handlers because the inbox uses the key for per-handler deduplication tracking. Without a key, the application throws `InvalidOperationException` at startup.

## Root Cause

When `UseInbox<TDbContext>()` is called on a consume channel, all handlers on that channel become inbox-managed. Inbox-managed handlers require a stable string key passed to `WithHandler<T>("key")`. The key-less overload `WithHandler<T>()` is only valid for fire-and-forget handlers (channels without inbox).

Startup validation in `InboxConfigurationValidator` catches this and throws immediately.

## Solution

Always provide a handler key when the channel uses inbox:

```csharp
// FIXED — handler key provided
bus.AddEventConsumeChannel("orders.events", c => c
    .WithRabbitMq(r => r.WithQueueName("orders.events.subscriptions"))
    .Consumes<OrderPlaced>(m => m.WithHandler<OrderPlacedHandler>("order-placed"))
    .UseInbox<OrderDbContext>());
```

## Prevention

- When writing example code with `UseInbox`, always verify every `WithHandler` call includes a string key
- The two registration patterns are: `WithHandler<T>()` (fire-and-forget) vs `WithHandler<T>("key")` (inbox-managed) — mixing them on an inbox channel is a startup error
- DocFX code snippets pull from the example project via `#region` markers, so a broken example propagates to multiple documentation pages simultaneously
