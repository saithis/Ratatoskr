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

var rabbitmq = builder
    .AddRabbitMQ("rabbitmq", password: rabbitmqPassword)
    .WithManagementPlugin()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("rabbitmq-ceb");

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
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("RATATOSKR_EXAMPLES_PLAYGROUND", "1")
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();
