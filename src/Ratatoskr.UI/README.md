# Ratatoskr.UI

Embedded management web dashboard for the Ratatoskr CloudEventBus. Provides cross-service visualization of outbox and inbox health, message inspection, and retry/discard capabilities across all connected microservices.

## Features

- **Embedded Zero-NPM Web Dashboard**: Zero external Node.js build dependencies. Static HTML5, CSS3, and vanilla ES modules packaged directly into `Ratatoskr.UI.dll`.
- **Real-Time Live Updates**: Server-Sent Events (SSE) stream service heartbeats, status changes, and outbox/inbox backlog counters directly to connected browsers.
- **Standalone or Embedded Hosting**: Can run as an independent microservice dashboard or be mounted alongside an existing ASP.NET Core service.
- **Broker-First RPC**: Communicates directly through RabbitMQ using 2 exchanges (`{uiUser}.commands` and `{uiUser}.inbox`), avoiding complex HTTP/ingress firewall policies between internal microservices.
- **EF Core In-Process Support**: Also works for in-process monolithic or modular setups using the EF Core transport without requiring RabbitMQ.
- **Mandatory Policy Authorization**: Compile-time requirement for an ASP.NET Core authorization policy name when mounting routes (`app.MapRatatoskrUI("AdminPolicy", "/ratatoskr")`).

## Getting Started

Install the package via NuGet:

```bash
dotnet add package Ratatoskr.UI
```

Register and map the dashboard in `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register RabbitMQ (if running over broker)
builder.Services.AddRatatoskr(bus =>
{
    bus.UseRabbitMq(c => c.ConnectionString = new Uri("amqp://..."));
});

// Configure authorization policy
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("RatatoskrAdmin", policy => policy.RequireRole("Admin"));

// Register Ratatoskr UI
builder.Services.AddRatatoskrUI(options =>
{
    options.UiExchangePrefix = "ratatoskr.ui";
    options.RequestTimeout = TimeSpan.FromSeconds(15);
    options.ServiceOfflineThreshold = TimeSpan.FromSeconds(45);
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Mount the dashboard (e.g. at /ratatoskr)
app.MapRatatoskrUI("RatatoskrAdmin", "/ratatoskr");

app.Run();
```

Open `http://localhost:<port>/ratatoskr` in your browser to view the management dashboard.
