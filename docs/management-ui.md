# Management UI

The `Ratatoskr.UI` package adds an Angular-based management dashboard that aggregates the management APIs of one or more Ratatoskr services into a single UI. Use it to inspect and act on poisoned outbox and inbox messages across services without writing SQL.

## Installation

```bash
dotnet add package Ratatoskr.UI
```

## Concepts

### Backends

A **backend** is a Ratatoskr service that exposes the management API. The UI supports two kinds:

| Kind | When to use |
|------|-------------|
| **Local** | The API is hosted in the same process as the UI |
| **Remote** | The API is hosted in a separate service, reached over HTTP |

The dashboard fan-out calls `GET /ratatoskr/api/v1/efcore/contexts` on each backend to collect health data. Local backends are dispatched in-process; remote backends are dispatched via `HttpClient`.

### In-process dispatch

For local backends, requests are forwarded directly through the ASP.NET Core middleware pipeline — no HTTP round-trip. The caller's `ClaimsPrincipal` is propagated to the synthetic request, so the management API's authorization policy evaluates the same authenticated user that hit the proxy route.

## Setup

### 1. Register services

```csharp
builder.Services.AddRatatoskrUi();
```

### 2. Configure the middleware

Place `UseRatatoskrUi` **before** `UseRouting`, `UseAuthentication`, and `UseAuthorization`. The middleware captures the downstream pipeline on the first request to enable in-process dispatch.

```csharp
app.UseRatatoskrUi(options =>
{
    options.BasePath = "/ratatoskr";       // default
    options.PolicyName = "RatatoskrAdmin"; // leave empty for open access

    // Local backend — same process
    options.AddLocalBackend("MyService");

    // Remote backend — separate service
    options.AddBackend("Orders", "https://orders-service");
    options.AddBackend("Billing", "https://billing-service", auth =>
        auth.AddBearerToken(() => FetchServiceTokenAsync()));
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRatatoskrManagementApi("RatatoskrAdmin");
app.MapRatatoskrUiRoutes();
```

> [!IMPORTANT]
> `UseRouting()` must be called **explicitly** after `UseRatatoskrUi()` so that in-process dispatch re-routes synthetic requests correctly. If you rely on `WebApplication`'s automatic routing insertion (no explicit `UseRouting()` call), the captured pipeline will not include routing, which causes incorrect endpoint selection for dispatched requests.

### 3. Register the management API

The management API is a prerequisite. The UI proxy forwards backend requests to it:

```csharp
app.MapRatatoskrManagementApi("RatatoskrAdmin");
app.MapRatatoskrUiRoutes();
```

## Backend configuration

### Local backend

```csharp
options.AddLocalBackend("MyService");
```

Requests to `{basePath}/api/v1/backends/MyService/{**rest}` are dispatched in-process to `{basePath}/api/v1/{rest}`.

### Remote backend

```csharp
options.AddBackend("Orders", "https://orders-service");
```

Requests are forwarded to `https://orders-service/ratatoskr/api/v1/{rest}` using an `IHttpClientFactory`-managed `HttpClient`.

### Remote backend with authentication

```csharp
options.AddBackend("Orders", "https://orders-service", auth =>
    auth.AddBearerToken(() => GetTokenAsync()));
```

The auth delegate receives the outgoing `HttpRequestMessage` and can set any headers or properties needed to authenticate the service-to-service call.

## Authorization

Set `PolicyName` to require authorization on all proxy routes:

```csharp
options.PolicyName = "RatatoskrAdmin";
```

Leave it empty (the default) to allow anonymous access to the proxy routes — useful for development or when authorization is handled upstream (e.g., an API gateway).

## Proxy routes

The UI package maps the following routes under `{basePath}/api/v1`:

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/backends` | List registered backends |
| `GET` | `/dashboard` | Parallel health fan-out across all backends |
| `ANY` | `/backends/{name}/{**rest}` | Transparent passthrough to a specific backend |

The SPA itself is served at `{basePath}/**` (Angular `HashLocationStrategy`).

## Optional activation (`IfConfigured` variants)

Shared test hosts and library authors can use the no-op variants that activate only when the UI is configured:

```csharp
app.UseRatatoskrUiIfConfigured();
app.UseRouting();
// ...
app.MapRatatoskrUiRoutesIfConfigured();
```

These are also useful when the same host is shared between tests that include the UI and tests that do not.

### Pre-configuring options for tests

```csharp
services.AddRatatoskrUi(options =>
{
    options.PolicyName = "RatatoskrAdmin";
    options.AddLocalBackend("TestService");
});
```

This overload stores the configured options in the DI container so `UseRatatoskrUiIfConfigured` and `MapRatatoskrUiRoutesIfConfigured` activate automatically.

## Development workflow

During development, the Angular dev server handles the SPA assets. Start it in the `ClientApp` directory:

```bash
cd src/Ratatoskr.UI/ClientApp
npm ci
npx ng serve
```

The Angular dev server proxies API calls to the .NET backend. No Angular build is required for the .NET backend to work — static files are silently skipped when no production build exists.

## Angular assets (production)

The Angular SPA is compiled into `wwwroot/` and embedded in the NuGet package as a `ManifestEmbeddedFileProvider`. The build runs automatically in `Release` configuration:

```bash
dotnet build -c Release src/Ratatoskr.UI/Ratatoskr.UI.csproj
```

To force the Angular build in `Debug` mode:

```bash
dotnet build -p:BuildAngular=true src/Ratatoskr.UI/Ratatoskr.UI.csproj
```

## Multi-service example

A single UI aggregating a local service and a remote `Orders` service:

```csharp
// Program.cs
builder.Services.AddRatatoskrUi();
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization(o =>
    o.AddPolicy("RatatoskrAdmin", p => p.RequireRole("ops")));

var app = builder.Build();

app.UseRatatoskrUi(options =>
{
    options.PolicyName = "RatatoskrAdmin";
    options.AddLocalBackend("Playground");
    options.AddBackend("Orders", builder.Configuration["Services:Orders"]!,
        auth => auth.AddBearerToken(() => GetInternalTokenAsync()));
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRatatoskrManagementApi("RatatoskrAdmin");
app.MapRatatoskrUiRoutes();

app.Run();
```
