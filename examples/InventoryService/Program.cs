// Local development example only. See examples/README.md.
//
// A second Ratatoskr service that hosts no dashboard of its own. It only exposes the management
// API, which the PlaygroundHost dashboard aggregates through
// AddRatatoskrUI(o => o.AddService("inventory", ...)).
using InventoryService;
using InventoryService.Handlers;
using InventoryService.Messages;
using InventoryService.Persistence;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.EntityFrameworkCore;
using Ratatoskr;
using Ratatoskr.EfCore;
using Ratatoskr.Management;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDistributedLockProvider>(
    _ => new FileDistributedSynchronizationProvider(new DirectoryInfo(Environment.CurrentDirectory))
);

// The management API always requires a policy. This example is local-only, so it authorizes
// everyone; a real deployment would require an operator role here.
builder
    .Services.AddAuthorizationBuilder()
    .AddPolicy("DevOnlyNoAuth", p => p.RequireAssertion(_ => true));

builder.Services.AddRatatoskr(bus =>
{
    // Two DbContexts on one service, which is what the dashboard's DbContext picker switches
    // between. InventoryDbContext runs both halves; AuditDbContext runs an outbox only.
    bus.AddEfCoreDurability<InventoryDbContext>(d =>
    {
        d.UseOutbox(o =>
            o.WithPollingInterval(TimeSpan.FromMilliseconds(300))
                .WithMaxRetries(3)
                .WithMaxRetryDelay(TimeSpan.FromSeconds(1))
        );
        d.UseInbox(i =>
            i.WithPollingInterval(TimeSpan.FromMilliseconds(300))
                .WithMaxRetries(2)
                .WithMaxRetryDelay(TimeSpan.FromSeconds(1))
        );
    });

    bus.AddEfCoreDurability<AuditDbContext>(d =>
        d.UseOutbox(o =>
            o.WithPollingInterval(TimeSpan.FromMilliseconds(300))
                .WithMaxRetries(3)
                .WithMaxRetryDelay(TimeSpan.FromSeconds(1))
        )
    );

    // No broker: this service uses the EF Core transport, so it only needs Postgres.
    bus.AddCommandPublishChannel(
        "inventory.commands",
        c => c.WithEfCore().Produces<ReserveStock>()
    );
    bus.AddCommandConsumeChannel(
        "inventory.commands",
        c =>
            c.Consumes<ReserveStock>(m =>
                    m.WithHandler<ReserveStockHandler>("inventory.reserve-stock")
                )
                .UseInbox<InventoryDbContext>()
    );

    // Staged in AuditDbContext but delivered into InventoryDbContext's inbox, so the audit
    // outbox actually produces rows instead of being optimized into the same transaction.
    bus.AddEventPublishChannel("inventory.audit", c => c.WithEfCore().Produces<StockAudited>());
    bus.AddEventConsumeChannel(
        "inventory.audit",
        c =>
            c.Consumes<StockAudited>(m => m.WithHandler<StockAuditedHandler>("inventory.audit"))
                .UseInbox<InventoryDbContext>()
    );
});

var inventoryCs =
    builder.Configuration.GetConnectionString("inventorydb")
    ?? throw new InvalidOperationException("Connection string 'inventorydb' is not configured.");
var auditCs =
    builder.Configuration.GetConnectionString("auditdb")
    ?? throw new InvalidOperationException("Connection string 'auditdb' is not configured.");

builder.Services.AddDbContext<InventoryDbContext>(
    (sp, options) =>
    {
        options.UseNpgsql(inventoryCs);
        options.RegisterOutbox<InventoryDbContext>(sp);
    }
);

builder.Services.AddDbContext<AuditDbContext>(
    (sp, options) =>
    {
        options.UseNpgsql(auditCs);
        options.RegisterOutbox<AuditDbContext>(sp);
    }
);

var app = builder.Build();

app.MapDefaultEndpoints();
app.UseAuthorization();

app.MapRatatoskrManagementApi("DevOnlyNoAuth");

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope
        .ServiceProvider.GetRequiredService<InventoryDbContext>()
        .Database.EnsureCreatedAsync();
    await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.EnsureCreatedAsync();
}

app.MapPost(
    "/inventory/reservations",
    async (
        ReserveStock request,
        InventoryDbContext inventoryDb,
        AuditDbContext auditDb,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    ) =>
    {
        // Same DbContext for publish and consume, so Ratatoskr writes the inbox row inside this
        // very transaction instead of taking a detour through the outbox.
        inventoryDb.OutboxMessages.Add(request);
        await inventoryDb.SaveChangesAsync(cancellationToken);

        // Different DbContext for publish and consume, so this one does go through the outbox.
        auditDb.OutboxMessages.Add(
            new StockAudited(request.Sku, request.Quantity, timeProvider.GetUtcNow())
        );
        await auditDb.SaveChangesAsync(cancellationToken);

        return TypedResults.Accepted((string?)null, request);
    }
);

app.MapGet(
    "/inventory/reservations",
    async (InventoryDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(
            await db
                .Reservations.OrderByDescending(r => r.ReservedAt)
                .Take(50)
                .ToListAsync(cancellationToken)
        )
);

await app.RunAsync();
