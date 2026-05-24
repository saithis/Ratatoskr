using System.Text;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Ratatoskr.RabbitMq;
using Ratatoskr.TestHost;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration;

[ClassDataSource<RabbitMqContainerFixture, PostgresContainerFixture>(
    Shared = [SharedType.PerTestSession, SharedType.PerTestSession]
)]
public abstract class RatatoskrIntegrationTest(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : IAsyncDisposable
{
    private WebApplicationFactory<RatatoskrTestHostAppMarker>? _factory;

    /// <summary>
    /// Provides access to the application's root service provider.
    /// Available after <see cref="StartTestAsync"/> has been called.
    /// </summary>
    protected IServiceProvider Services =>
        _factory?.Services
        ?? throw new InvalidOperationException("StartTestAsync has not been called yet.");

    /// <summary>
    /// Unique ID for this test instance to isolate resources
    /// </summary>
    protected string TestId { get; } = Guid.NewGuid().ToString("N");
    protected string RabbitMqConnectionString => rabbitMq.ConnectionString;

    /// <summary>
    /// Override the connection string to point to the unique database for this test
    /// </summary>
    protected string PostgresConnectionString
    {
        get
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                Database = $"test_{TestId}",
                MaxPoolSize = 2,
            };
            return builder.ToString();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "WebApplicationFactory ownership is transferred to _factory and disposed in DisposeAsyncCore."
    )]
    public virtual async Task StartTestAsync(Action<IServiceCollection>? configure = null)
    {
        await CreateDatabaseAsync();
        // Hosted services (e.g. EF Core metrics) query the DbContext as soon as the host starts.
        // EnsureCreated must run before the host is built so tables exist on first poll.
        await EnsureTestDatabaseSchemaAsync();

        if (_factory != null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        _factory = new RatatoskrTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ConfigureServices(services);
                configure?.Invoke(services);
            });
        });

        // Create the scope from the factory's services
        await using var scope = _factory.Services.CreateAsyncScope();
        var topologyManager = scope.ServiceProvider.GetService<RabbitMqTopologyManager>();
        if (topologyManager != null)
        {
            // Wait for topology provisioning to complete
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await topologyManager.WaitForProvisioningAsync(cts.Token);
        }
    }

    private async Task CreateDatabaseAsync()
    {
        // Connect to the maintenance database (usually 'postgres' or the one from fixture) to create the new one
        await using var connection = new Npgsql.NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"test_{TestId}\"";
        await command.ExecuteNonQueryAsync();
    }

    private async Task EnsureTestDatabaseSchemaAsync()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        await using var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        var lockFileDirectory = new DirectoryInfo(
            Path.Combine(Environment.CurrentDirectory, TestId)
        ); // choose where the lock files will live
        lockFileDirectory.Create();
        services.AddSingleton<IDistributedLockProvider>(
            _ => new FileDistributedSynchronizationProvider(lockFileDirectory)
        );
    }

    /// <summary>
    /// Creates an HTTP client that can make requests to the test server.
    /// Requires <see cref="StartTestAsync"/> to have been called first.
    /// </summary>
    protected HttpClient CreateHttpClient()
    {
        if (_factory is null)
        {
            throw new InvalidOperationException("StartTestAsync has not been called yet.");
        }

        return _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        await DropDatabaseAsync();
    }

    private async Task DropDatabaseAsync()
    {
        try
        {
            // Connect to the maintenance database to drop the test database
            await using var connection = new Npgsql.NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            // Force drop by terminating other connections if any exist (though there shouldn't be any at this point)
            command.CommandText = $"DROP DATABASE IF EXISTS \"test_{TestId}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to drop database test_{TestId}: {ex.Message}");
            // Don't fail the test if cleanup fails, but log it
        }
    }

    // --- Helper Methods ---

    protected class ScopeContext
    {
        public required IServiceProvider ServiceProvider { get; set; }
    }

    protected async Task InScopeAsync(Func<ScopeContext, Task> arrange)
    {
        if (_factory == null)
        {
            throw new InvalidOperationException($"Call {nameof(StartTestAsync)} first");
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        await arrange(new ScopeContext { ServiceProvider = scope.ServiceProvider });
    }

    protected async Task InScopeAsync(Action<ScopeContext> arrange) =>
        await InScopeAsync(ctx =>
        {
            arrange(ctx);
            return Task.CompletedTask;
        });

    protected async Task<TRes> InScopeAsync<TRes>(Func<ScopeContext, Task<TRes>> arrange)
    {
        if (_factory == null)
        {
            throw new InvalidOperationException($"Call {nameof(StartTestAsync)} first");
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        return await arrange(new ScopeContext { ServiceProvider = scope.ServiceProvider });
    }

    protected async Task<TRes> InScopeAsync<TRes>(Func<ScopeContext, TRes> arrange) =>
        await InScopeAsync(ctx =>
        {
            var result = arrange(ctx);
            return Task.FromResult(result);
        });

    protected async Task PublishToRabbitMqAsync<T>(
        string exchange,
        string routingKey,
        T message,
        string? type = null
    )
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(message);
        var body = Encoding.UTF8.GetBytes(json);

        var props = new BasicProperties
        {
            MessageId = Guid.NewGuid().ToString(),
            Type =
                type
                ?? System
                    .Reflection.CustomAttributeExtensions.GetCustomAttribute<RatatoskrMessageAttribute>(
                        typeof(T)
                    )
                    ?.Type
                ?? throw new InvalidOperationException(
                    $"Message type is required for {typeof(T).Name}."
                ),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
        };

        await channel.BasicPublishAsync(exchange, routingKey, false, props, body);
    }

    protected async Task<uint> GetMessageCountAsync(string queueName)
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        try
        {
            return await channel.MessageCountAsync(queueName);
        }
        catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            // Queue might not exist
            return 0;
        }
    }

    protected async Task<BasicGetResult?> GetMessageAsync(string queueName)
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        return await channel.BasicGetAsync(queueName, autoAck: true);
    }

    protected async Task EnsureQueueBoundAsync(string queueName, string exchange, string routingKey)
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: false,
            exclusive: false,
            autoDelete: true
        );

        if (!string.IsNullOrEmpty(exchange))
        {
            await channel.ExchangeDeclarePassiveAsync(exchange);
            await channel.QueueBindAsync(queueName, exchange, routingKey);
        }
    }

    protected async Task InitializeDatabase() =>
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.EnsureCreatedAsync();
        });

    protected Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string? message = null
    ) => WaitForConditionAsync(() => Task.FromResult(condition()), timeout, message);

    protected static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        string? message = null
    )
    {
        var start = DateTime.UtcNow;
        while (!await condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException(message ?? "Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }
}
