# Ratatoskr AppHost

This .NET Aspire AppHost orchestrates the e-commerce playground: **PlaygroundHost**, PostgreSQL (publisher, consumer, and playground logical databases), and RabbitMQ.

See [examples/README.md](../README.md) for the full demo guide.

## Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL and RabbitMQ containers)
- [Aspire CLI](https://aspire.dev) (`aspire` command)

## Running

```bash
cd examples/AppHost
aspire run
```

The Aspire dashboard opens (often at http://localhost:15000). Open the **playgroundhost** resource URL for the playground UI.
