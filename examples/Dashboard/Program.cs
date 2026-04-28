// Local development example only. See examples/README.md.
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapStaticAssets();

// Management API base path — must match ManagementApiEndpointExtensions.DefaultBasePath
const string mgmtBase = "ratatoskr/api/v1/efcore/contexts";

app.MapGet("/api/config", (IConfiguration config) =>
{
    var orderServiceUrl = config["OrderService__ManagementUrl"]
        ?? throw new InvalidOperationException("OrderService__ManagementUrl is not configured.");
    var inventoryServiceUrl = config["InventoryService__ManagementUrl"]
        ?? throw new InvalidOperationException("InventoryService__ManagementUrl is not configured.");

    return TypedResults.Ok(new
    {
        orderServiceUrl,
        orderManagementPath = $"{mgmtBase}/OrdersDbContext",
        inventoryServiceUrl,
        inventoryManagementPath = $"{mgmtBase}/InventoryDbContext",
    });
});

app.MapFallbackToFile("index.html");

app.Run();
