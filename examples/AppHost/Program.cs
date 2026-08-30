var builder = DistributedApplication.CreateBuilder(args);

var postgresPassword = builder.AddParameter("postgres-password", "guest", secret: false);
var rabbitmqPassword = builder.AddParameter("rabbitmq-password", "guest", secret: false);

var postgres = builder
    .AddPostgres("postgres", password: postgresPassword)
    .WithPgAdmin(pga => pga.WithLifetime(ContainerLifetime.Persistent))
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("postgres-ceb");

var publisherDb = postgres.AddDatabase("publisherdb");
var consumerDb = postgres.AddDatabase("consumerdb");
var playgroundDb = postgres.AddDatabase("playgrounddb");
var inventoryDb = postgres.AddDatabase("inventorydb");
var auditDb = postgres.AddDatabase("auditdb");

var rabbitmq = builder
    .AddRabbitMQ("rabbitmq", password: rabbitmqPassword)
    .WithManagementPlugin()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("rabbitmq-ceb");

// Second Ratatoskr service. It hosts no dashboard of its own; the PlaygroundHost dashboard
// aggregates it over the management API, which is what multi-service mode looks like.
var inventoryService = builder
    .AddProject<Projects.InventoryService>("inventoryservice")
    .WithReference(inventoryDb)
    .WaitFor(inventoryDb)
    .WithReference(auditDb)
    .WaitFor(auditDb)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpHealthCheck("/health/ready");

builder
    .AddProject<Projects.PlaygroundHost>("playgroundhost")
    .WithReference(publisherDb)
    .WaitFor(publisherDb)
    .WithReference(consumerDb)
    .WaitFor(consumerDb)
    .WithReference(playgroundDb)
    .WaitFor(playgroundDb)
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq)
    // Hands the playground host a service discovery URL for the inventory service, which it
    // passes to AddRatatoskrUI so the dashboard can relay to it.
    .WithReference(inventoryService)
    .WaitFor(inventoryService)
    .WithEnvironment("InventoryService__Url", inventoryService.GetEndpoint("http"))
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("RATATOSKR_EXAMPLES_PLAYGROUND", "1")
    .WithHttpHealthCheck("/health/ready");

await builder.Build().RunAsync();
