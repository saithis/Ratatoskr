<p align="center">
  <img src="images/banner_outlined.png" alt="Ratatoskr" />
</p>

# Ratatoskr

  A modern .NET library for reliable event/message publishing using the outbox pattern with CloudEvents support.

## Getting Started

### Quick Start with .NET Aspire

The easiest way to run the example application is using the .NET Aspire AppHost.

First, install the Aspire workload if you haven't already:

```bash
dotnet workload install aspire
```

Then run the AppHost:

```bash
cd examples/AppHost
dotnet run
```

This will start:
- PostgreSQL databases (ordersdb, inventorydb)
- RabbitMQ message broker
- OrderService, InventoryService, NotificationService, Dashboard
- Aspire Dashboard at http://localhost:15000

See [examples/README.md](examples/README.md) for the full demo guide.

## Project Structure

- `src/Ratatoskr` - Core library
- `src/Ratatoskr.EfCore` - Entity Framework Core outbox/inbox implementation
- `src/Ratatoskr.RabbitMq` - RabbitMQ transport
- `examples/` - E-commerce playground (OrderService, InventoryService, NotificationService, Dashboard)
- `examples/AppHost` - .NET Aspire orchestration
- `tests/Ratatoskr.Tests` - Integration and unit tests

## Testing

The project includes comprehensive tests using TUnit and TestContainers:

```bash
cd tests/Ratatoskr.Tests
dotnet run
```

- Integration tests with real PostgreSQL and RabbitMQ containers
- See [tests/TESTING.md](tests/TESTING.md) for detailed testing guide