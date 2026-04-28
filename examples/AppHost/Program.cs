var builder = DistributedApplication.CreateBuilder(args);

var postgresPassword = builder.AddParameter("postgres-password", "guest", secret: false);
var rabbitmqPassword = builder.AddParameter("rabbitmq-password", "guest", secret: false);

var postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithPgAdmin()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("postgres-ceb");

var ordersDb    = postgres.AddDatabase("ordersdb");
var inventoryDb = postgres.AddDatabase("inventorydb");

var rabbitmq = builder.AddRabbitMQ("rabbitmq", password: rabbitmqPassword)
    .WithManagementPlugin()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("rabbitmq-ceb");

var notificationSvc = builder.AddProject<Projects.NotificationService>("notificationservice")
    .WithReference(rabbitmq).WaitFor(rabbitmq);

var inventorySvc = builder.AddProject<Projects.InventoryService>("inventoryservice")
    .WithReference(inventoryDb).WaitFor(inventoryDb)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WithHttpHealthCheck("/health/ready");

// Wait for consumers to declare queue bindings before OrderService starts publishing.
// If OrderService publishes before InventoryService/NotificationService have bound their queues,
// the broker silently drops the messages.
var orderSvc = builder.AddProject<Projects.OrderService>("orderservice")
    .WithReference(ordersDb).WaitFor(ordersDb)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WaitFor(inventorySvc)
    .WaitFor(notificationSvc)
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.Dashboard>("dashboard")
    .WithReference(orderSvc)
    .WithReference(inventorySvc)
    .WithEnvironment("OrderService__ManagementUrl", orderSvc.GetEndpoint("https"))
    .WithEnvironment("InventoryService__ManagementUrl", inventorySvc.GetEndpoint("https"))
    .WaitFor(orderSvc)
    .WaitFor(inventorySvc)
    .WaitFor(notificationSvc);

builder.Build().Run();
