<p align="center">
  <img src="images/banner_outlined.png" alt="Ratatoskr" />
</p>

# Ratatoskr

  A modern .NET library for reliable event/message publishing using the outbox pattern with CloudEvents support.

## Getting Started

### Quick Start with .NET Aspire

The easiest way to run the example application is using the .NET Aspire AppHost (Aspire CLI, not the obsolete Aspire workload).

```bash
aspire run
```

This starts PostgreSQL (logical databases `publisherdb`, `consumerdb`, `playgrounddb`), RabbitMQ, and the **PlaygroundHost** web app, plus the Aspire dashboard (often at http://localhost:15000).

See [examples/README.md](examples/README.md) for the full demo guide.

## Project Structure

- `src/Ratatoskr` - Core library (channels, serialization, CloudEvents, management agent runtime, and in-process control plane)
- `src/Ratatoskr.EfCore` - Entity Framework Core outbox/inbox durability and management operations
- `src/Ratatoskr.RabbitMq` - RabbitMQ application transport and DLQ management operations
- `src/Ratatoskr.Management.Abstractions` - Management protocol contracts and transport abstractions
- `src/Ratatoskr.Management.RabbitMq` - RabbitMQ management transport provider for distributed control planes
- `src/Ratatoskr.UI` - Embedded management web dashboard
- `examples/` - Playground (`PlaygroundHost` + AppHost)
- `examples/AppHost` - .NET Aspire orchestration
- `tests/Ratatoskr.Tests` - Integration and unit tests

## Testing

The project includes comprehensive tests using TUnit and TestContainers:

```bash
dotnet run --project tests/Ratatoskr.Tests -- --maximum-parallel-tests 10
```

- Integration tests with real PostgreSQL and RabbitMQ containers
- See [tests/TESTING.md](tests/TESTING.md) for detailed testing guide