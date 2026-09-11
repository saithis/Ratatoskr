# Ratatoskr.UI

Embedded management web dashboard for the Ratatoskr CloudEventBus. Provides cross-service visualization of outbox and inbox health, message inspection, and retry/discard capabilities across all connected microservices.

## Features

- **Embedded Zero-NPM Web Dashboard**: Zero external Node.js build dependencies. Static HTML5, CSS3, and vanilla ES modules packaged directly into `Ratatoskr.UI.dll`.
- **Real-Time Live Updates**: Server-Sent Events (SSE) stream service heartbeats, status changes, and outbox/inbox backlog counters directly to connected browsers.
- **Standalone or Embedded Hosting**: Can run as an independent microservice dashboard or be mounted alongside an existing ASP.NET Core service.
- **Transport-neutral management**: Depends on `IManagementClient` and `IServiceCatalog`, not a message broker.
- **In-process support**: Works for co-hosted modular monoliths without installing a broker package.
- **Mandatory Policy Authorization**: Compile-time requirement for an ASP.NET Core authorization policy name when mounting routes (`app.MapRatatoskrUI("AdminPolicy", "/ratatoskr")`).

## Getting Started

Install the package via NuGet:

```bash
dotnet add package Ratatoskr.UI
```

Register and map the dashboard in `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRatatoskr(bus =>
{
});

builder.Services.AddRatatoskrManagement(); // includes the in-process provider

// Configure authorization policy
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("RatatoskrAdmin", policy => policy.RequireRole("Admin"));

// Register Ratatoskr UI
builder.Services.AddRatatoskrUI();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Mount the dashboard (e.g. at /ratatoskr)
app.MapRatatoskrUI("RatatoskrAdmin", "/ratatoskr");

app.Run();
```

Open `http://localhost:<port>/ratatoskr` in your browser to view the management dashboard.

For a distributed dashboard, install and register a management provider (for example,
`AddRabbitMqManagement`) in both the dashboard and managed-service processes. The UI itself
does not reference a broker package.
