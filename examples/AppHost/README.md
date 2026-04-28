# Ratatoskr AppHost

This .NET Aspire AppHost orchestrates the e-commerce playground — four services, two PostgreSQL databases, and a RabbitMQ broker.

See [examples/README.md](../README.md) for the full demo guide.

## Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL and RabbitMQ containers)
- .NET Aspire workload: `dotnet workload install aspire`

## Running

```bash
cd examples/AppHost
dotnet run
```

The Aspire Dashboard opens at http://localhost:15000. From there, find the **dashboard** service URL to open the playground UI.
