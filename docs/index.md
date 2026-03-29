---
_layout: landing
---

<div class="hero">
  <div class="hero-inner">
    <div class="hero-text">
      <h1><span class="accent">Ratatoskr</span></h1>
      <p class="tagline">
        Reliable event-driven messaging for .NET — transport-agnostic publishing, durable Outbox &amp; Inbox patterns, and native CloudEvents support.
      </p>
      <div class="hero-actions">
        <a href="getting-started.md" class="btn-hero primary">Get Started</a>
        <a href="architecture.md" class="btn-hero secondary">Architecture</a>
        <a href="api/" class="btn-hero secondary">API Reference</a>
      </div>
    </div>
    <div class="hero-mascot">
      <img src="images/mascot_lines_glow.png" alt="Ratatoskr — the cyber squirrel messenger">
    </div>
  </div>
</div>

<div class="install-banner">
  <div class="install-banner-inner">
    <span>Install via NuGet:</span>
    <code>dotnet add package Ratatoskr</code>
  </div>
</div>

<div class="features">
  <h2>Why Ratatoskr?</h2>
  <p class="section-subtitle">Everything you need for reliable, observable messaging in .NET applications.</p>
  <div class="feature-grid">
    <div class="feature-card">
      <span class="feature-icon">&#x1f4e1;</span>
      <h3>Channel-First Design</h3>
      <p>Declare channels with intent — event publish, command consume — and attach message types, handlers, and transports in one place.</p>
    </div>
    <div class="feature-card">
      <span class="feature-icon">&#x1f512;</span>
      <h3>Transactional Outbox</h3>
      <p>Messages are persisted in the same database transaction as your business data. Nothing is lost, even if the broker goes down.</p>
    </div>
    <div class="feature-card">
      <span class="feature-icon">&#x1f4e5;</span>
      <h3>Durable Inbox</h3>
      <p>Per-handler deduplication and retry with persistent state via EF Core. Poisoned messages are quarantined, not dropped.</p>
    </div>
    <div class="feature-card">
      <span class="feature-icon">&#x2601;</span>
      <h3>CloudEvents Native</h3>
      <p>Messages follow the <a href="https://cloudevents.io/">CloudEvents</a> specification with W3C trace context propagation out of the box.</p>
    </div>
    <div class="feature-card">
      <span class="feature-icon">&#x1f500;</span>
      <h3>Multiple Transports</h3>
      <p>RabbitMQ for production messaging, EF Core for broker-free development — swap transports without changing your handlers.</p>
    </div>
    <div class="feature-card">
      <span class="feature-icon">&#x1f4ca;</span>
      <h3>Observable</h3>
      <p>Built-in OpenTelemetry tracing and metrics. See every message flow through your system end to end.</p>
    </div>
    <div class="feature-card">
      <span class="feature-icon">&#x2696;</span>
      <h3>Horizontally Scalable</h3>
      <p>Distributed locks and optimistic concurrency let you run multiple instances safely without double-processing.</p>
    </div>
    <div class="feature-card">
      <span class="feature-icon">&#x1f9ea;</span>
      <h3>Testable</h3>
      <p><code>Ratatoskr.Testing</code> provides trace-isolated parallel test sessions — integration tests that run fast and don't interfere.</p>
    </div>
  </div>
</div>

<div class="packages">
  <h2>Packages</h2>
  <div class="package-grid">
    <div class="package-item">
      <code>Ratatoskr</code>
      <p>Core abstractions, message routing, CloudEvents support, and serialization.</p>
    </div>
    <div class="package-item">
      <code>Ratatoskr.EfCore</code>
      <p>Outbox and Inbox durability patterns with EF Core transport.</p>
    </div>
    <div class="package-item">
      <code>Ratatoskr.RabbitMq</code>
      <p>RabbitMQ transport with topology management, retry, and DLQ support.</p>
    </div>
    <div class="package-item">
      <code>Ratatoskr.Testing</code>
      <p>Test utilities with W3C trace-isolated sessions and assertion helpers.</p>
    </div>
  </div>
</div>

<div class="quick-example">
  <h2>Quick Example</h2>
  <p class="section-subtitle">Define a message, handle it, wire it up — in minutes.</p>

Define a message:

[!code-csharp[](../examples/Docs/Messages/OrderPlaced.cs#OrderPlaced)]

Handle it:

[!code-csharp[](../examples/Docs/Handlers/OrderPlacedHandler.cs#OrderPlacedHandler)]

Configure channels and register handlers:

[!code-csharp[](../examples/Docs/Program.cs#AddRatatoskr)]

Publish directly or through the outbox:

[!code-csharp[](../examples/Docs/Program.cs#PublishDirectExample)]

[!code-csharp[](../examples/Docs/Program.cs#PublishOutboxExample)]

</div>

<div class="when-to-use">
  <h2>When to Use Ratatoskr</h2>
  <div class="use-grid">
    <div class="use-column good">
      <h3>Great fit</h3>
      <ul>
        <li>Reliable message delivery with transactional outbox guarantees</li>
        <li>Per-handler deduplication and retry via the inbox pattern</li>
        <li>CloudEvents-based messaging with standard metadata</li>
        <li>A channel-first API that enforces ownership and topology conventions</li>
      </ul>
    </div>
    <div class="use-column not-for">
      <h3>Not designed for</h3>
      <ul>
        <li>Request/reply or RPC patterns</li>
        <li>In-memory pub/sub without persistence requirements</li>
        <li>Saga or process manager orchestration (use MassTransit or Wolverine)</li>
        <li>Stream processing (use Kafka, EventStoreDB, or similar)</li>
      </ul>
    </div>
  </div>
</div>

<div class="cta-section">
  <h2>Ready to get started?</h2>
  <p>Build your first Ratatoskr application in under 10 minutes.</p>
  <div class="hero-actions">
    <a href="getting-started.md" class="btn-hero primary">Getting Started Guide</a>
    <a href="configuration.md" class="btn-hero secondary">Configuration Reference</a>
  </div>
</div>
