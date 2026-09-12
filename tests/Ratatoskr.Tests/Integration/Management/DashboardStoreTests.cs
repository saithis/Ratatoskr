using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ratatoskr.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;
using Ratatoskr.UI.Store;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// The dashboard's own database: how its schema is created, what it remembers across a restart,
/// and how it keeps from growing forever.
/// </summary>
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class DashboardStoreTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private const string Transport = "fake";

    private readonly List<string> _databases = [];

    [Test]
    public async Task Migrations_ApplyIntoADatabaseThatAlreadyHoldsApplicationTables()
    {
        // This is the case EnsureCreated silently gets wrong: it is all-or-nothing per database
        // and creates nothing at all when the database already exists, which is exactly what
        // happens when the dashboard points at an application database.
        var database = await CreateDatabaseAsync();
        await ExecuteAsync(
            database,
            """CREATE TABLE "Orders" ("Id" uuid PRIMARY KEY, "Total" numeric NOT NULL);"""
        );

        await using var context = CreateContext(database);
        await context.Database.MigrateAsync();

        var tables = await QueryTableNamesAsync(database);
        tables.Should().Contain("Orders", "the application's own table is untouched");
        tables.Should().Contain("RatatoskrDashboardServices");
        tables.Should().Contain("RatatoskrDashboardAudit");
    }

    [Test]
    public async Task Migrations_AreIdempotent()
    {
        var database = await CreateDatabaseAsync();

        await using (var first = CreateContext(database))
        {
            await first.Database.MigrateAsync();
        }

        await using var second = CreateContext(database);
        var act = async () => await second.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
        (await second.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task Startup_WithoutAutoMigrate_FailsClearlyWhenTheSchemaIsMissing()
    {
        // An empty dashboard with a database error buried in the logs is a worse first experience
        // than a startup failure that says what to do.
        var database = await CreateDatabaseAsync();

        await using var provider = BuildDashboard(database, autoMigrate: false, TimeProvider.System);
        var migrator = provider
            .GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains("Migration", StringComparison.Ordinal));

        var act = async () => await migrator.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("EnsureCreated");
    }

    [Test]
    public async Task Restart_ShowsTheLastKnownFleetImmediately_MarkedStaleByAge()
    {
        // A restarted dashboard that shows nothing until the next heartbeat looks exactly like a
        // total outage — during an incident, at the worst possible moment.
        var database = await CreateDatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));

        await using (var first = BuildDashboard(database, autoMigrate: true, clock))
        {
            var hosted = first.GetServices<IHostedService>().ToList();
            foreach (var service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }

            first.GetRequiredService<ServiceRegistry>().Publish(Transport, Announcement(clock));

            // The write-through happens off the announcement, so give it a moment to land.
            await WaitForRowAsync(database);

            foreach (var service in hosted)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        clock.Advance(TimeSpan.FromMinutes(10));

        await using var restarted = BuildDashboard(database, autoMigrate: true, clock);
        foreach (var service in restarted.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
        }

        var cards = restarted.GetRequiredService<ServiceRegistry>().GetServices();
        var card = cards.Should().ContainSingle().Subject;
        card.ServiceName.Should().Be("remembered-service");
        card.Liveness
            .Should()
            .Be(ServiceLiveness.Stale, "the replica has not announced since before the restart");
        card.LastSeenAt.Should().Be(clock.GetUtcNow().AddMinutes(-10));
    }

    [Test]
    public async Task AuditRetention_PrunesInBoundedBatches()
    {
        var database = await CreateDatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));

        await using var provider = BuildDashboard(database, autoMigrate: true, clock);
        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RatatoskrDashboardDbContext>();
            for (var index = 0; index < 1200; index++)
            {
                db.AuditEntries.Add(
                    new DashboardAuditEntry
                    {
                        Id = Guid.NewGuid(),
                        OperationId = Guid.NewGuid(),
                        TransportName = Transport,
                        ServiceName = "old-service",
                        Operation = ManagementOperationNames.OutboxRequeue,
                        StartedAt = clock.GetUtcNow().AddDays(-100),
                        CompletedAt = clock.GetUtcNow().AddDays(-100),
                        Outcome = nameof(ManagementResultStatus.Ok),
                    }
                );
            }

            db.AuditEntries.Add(
                new DashboardAuditEntry
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    TransportName = Transport,
                    ServiceName = "recent-service",
                    Operation = ManagementOperationNames.OutboxRequeue,
                    StartedAt = clock.GetUtcNow(),
                    CompletedAt = clock.GetUtcNow(),
                    Outcome = nameof(ManagementResultStatus.Ok),
                }
            );

            await db.SaveChangesAsync();
        }

        var cleanup = provider
            .GetServices<IHostedService>()
            .OfType<DashboardAuditCleanupService>()
            .Single();

        // More rows than one batch holds, so this also proves the loop continues past the first.
        var removed = await cleanup.CleanupAsync(CancellationToken.None);

        removed.Should().Be(1200);

        await using var verify = CreateContext(database);
        var remaining = await verify.AuditEntries.ToListAsync();
        remaining.Should().ContainSingle(entry => entry.ServiceName == "recent-service");
    }

    private static ServiceAnnouncement Announcement(TimeProvider clock) =>
        new()
        {
            ProtocolVersion = ManagementProtocol.Current,
            ServiceName = "remembered-service",
            InstanceId = "instance-1",
            MachineName = "pod-1",
            StartedAt = clock.GetUtcNow(),
            AnnouncedAt = clock.GetUtcNow(),
            Address = ManagementAddress.From(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["exchange"] = "x" }
            ),
        };

    private ServiceProvider BuildDashboard(string database, bool autoMigrate, TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddRatatoskrDashboard(dashboard =>
        {
            dashboard.UseStore(
                db => db.UseNpgsql(ConnectionStringFor(database)),
                store => store.AutoMigrate = autoMigrate
            );
            dashboard.Configure(options => options.StaleAfter = TimeSpan.FromSeconds(45));
        });

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
        );
    }

    private RatatoskrDashboardDbContext CreateContext(string database) =>
        new(
            new DbContextOptionsBuilder<RatatoskrDashboardDbContext>()
                .UseNpgsql(ConnectionStringFor(database))
                .Options
        );

    private async Task WaitForRowAsync(string database)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = CreateContext(database);
            if (await db.ServiceSnapshots.AnyAsync())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The announcement was never written through to the store.");
    }

    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = database,
            MaxPoolSize = 5,
        }.ToString();

    private async Task<string> CreateDatabaseAsync()
    {
        var database = $"dashstore_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
        _databases.Add(database);
        return database;
    }

    private async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryTableNamesAsync(string database)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringFor(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
        {
            try
            {
                await using var connection = new NpgsqlConnection(postgres.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
                await command.ExecuteNonQueryAsync();
            }
            catch (PostgresException)
            {
                // Leaving a test database behind is not worth failing teardown over.
            }
        }
    }
}
