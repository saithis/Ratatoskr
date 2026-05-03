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
    var orderServiceUrl = config["OrderService:ManagementUrl"]
        ?? throw new InvalidOperationException("OrderService:ManagementUrl is not configured.");
    var inventoryServiceUrl = config["InventoryService:ManagementUrl"]
        ?? throw new InvalidOperationException("InventoryService:ManagementUrl is not configured.");
    var notificationServiceUrl = config["NotificationService:BaseUrl"]
        ?? throw new InvalidOperationException("NotificationService:BaseUrl is not configured.");

    return TypedResults.Ok(new
    {
        orderServiceUrl,
        orderManagementPath = $"{mgmtBase}/OrdersDbContext",
        inventoryServiceUrl,
        inventoryManagementPath = $"{mgmtBase}/InventoryDbContext",
        notificationServiceUrl,
    });
});

app.MapFallbackToFile("index.html");

app.Run();
