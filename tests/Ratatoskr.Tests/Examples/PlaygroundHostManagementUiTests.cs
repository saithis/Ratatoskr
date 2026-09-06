using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using PlaygroundHost;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Examples;

[ClassDataSource<RabbitMqContainerFixture, PostgresContainerFixture>(
    Shared = [SharedType.PerTestSession, SharedType.PerTestSession]
)]
[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "Shared factory lifecycle.")]
[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP004:Don't ignore created IDisposable", Justification = "Test response lifecycle.")]
public sealed class PlaygroundHostManagementUiTests : IAsyncDisposable
{
    private static readonly SemaphoreSlim FactoryLock = new(1, 1);
    private static WebApplicationFactory<PlaygroundHostAppMarker>? _sharedFactory;

    private readonly RabbitMqContainerFixture _rabbit;
    private readonly PostgresContainerFixture _postgres;

    public PlaygroundHostManagementUiTests(
        RabbitMqContainerFixture rabbit,
        PostgresContainerFixture postgres
    )
    {
        _rabbit = rabbit;
        _postgres = postgres;
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Shared factory across tests.")]
    private async Task<WebApplicationFactory<PlaygroundHostAppMarker>> GetOrCreateFactoryAsync()
    {
        var existing = Volatile.Read(ref _sharedFactory);
        if (existing is not null)
        {
            return existing;
        }

        await FactoryLock.WaitAsync();
        try
        {
            existing = Volatile.Read(ref _sharedFactory);
            if (existing is not null)
            {
                return existing;
            }

            const string testId = "mgmt_ui_test";
            var pubDb = $"ph_{testId}_pub";
            var conDb = $"ph_{testId}_con";
            var playDb = $"ph_{testId}_play";
            var maint = MaintenanceConnectionString(_postgres.ConnectionString);
            await CreateDatabaseAsync(maint, pubDb);
            await CreateDatabaseAsync(maint, conDb);
            await CreateDatabaseAsync(maint, playDb);

            var pubCs = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
            {
                Database = pubDb,
            }.ToString();
            var conCs = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
            {
                Database = conDb,
            }.ToString();
            var playCs = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
            {
                Database = playDb,
            }.ToString();

            var factory = new WebApplicationFactory<PlaygroundHostAppMarker>().WithWebHostBuilder(
                builder =>
                {
                    builder.UseSetting("ConnectionStrings:rabbitmq", _rabbit.ConnectionString);
                    builder.UseSetting("ConnectionStrings:publisherdb", pubCs);
                    builder.UseSetting("ConnectionStrings:consumerdb", conCs);
                    builder.UseSetting("ConnectionStrings:playgrounddb", playCs);
                    builder.UseSetting("Playground:Enabled", "true");
                    builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
                }
            );

            _ = factory.Server;
            Volatile.Write(ref _sharedFactory, factory);
            return factory;
        }
        finally
        {
            FactoryLock.Release();
        }
    }

    private static string MaintenanceConnectionString(string fixtureCs)
    {
        var b = new NpgsqlConnectionStringBuilder(fixtureCs) { Database = "postgres", };
        return b.ToString();
    }

    private static async Task CreateDatabaseAsync(
        string maintenanceConnectionString,
        string databaseName
    )
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            // already exists
        }
    }

    [Test]
    public async Task ManagementUI_StaticAssets_ServedAtBasePath()
    {
        var factory = await GetOrCreateFactoryAsync();
        using var client = factory.CreateClient();

        // 1. Root index.html
        using var indexResp = await client.GetAsync("/ratatoskr/");
        indexResp.StatusCode.Should().Be(HttpStatusCode.OK);
        indexResp.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        var html = await indexResp.Content.ReadAsStringAsync();
        html.Should().Contain("Ratatoskr Management Dashboard");
        html.Should().Contain("Connected Services");

        // 2. CSS
        using var cssResp = await client.GetAsync("/ratatoskr/css/dashboard.css");
        cssResp.StatusCode.Should().Be(HttpStatusCode.OK);
        cssResp.Content.Headers.ContentType?.MediaType.Should().Be("text/css");

        // 3. JS
        using var jsResp = await client.GetAsync("/ratatoskr/js/app.js");
        jsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        jsResp.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
    }

    [Test]
    public async Task ManagementUI_ApiServices_ReturnsPlaygroundHostWithBothDbContexts()
    {
        var factory = await GetOrCreateFactoryAsync();
        using var client = factory.CreateClient();

        // Wait until playground-host registers its local heartbeat
        JsonElement? service = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var servicesResp = await client.GetAsync("/ratatoskr/api/services");
            if (servicesResp.StatusCode == HttpStatusCode.OK)
            {
                var services = await servicesResp.Content.ReadFromJsonAsync<JsonElement>();
                if (services.ValueKind == JsonValueKind.Array && services.GetArrayLength() > 0)
                {
                    foreach (var item in services.EnumerateArray())
                    {
                        if (string.Equals(item.GetProperty("serviceName").GetString(), "playground-host", StringComparison.OrdinalIgnoreCase))
                        {
                            service = item;
                            break;
                        }
                    }
                }
            }

            if (service is not null)
            {
                break;
            }

            await Task.Delay(250);
        }

        service.Should().NotBeNull();
        service!.Value.GetProperty("serviceName").GetString().Should().Be("playground-host");

        // Fetch detail
        using var detailResp = await client.GetAsync("/ratatoskr/api/services/playground-host");
        detailResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("serviceName").GetString().Should().Be("playground-host");

        var dbContexts = detail.GetProperty("dbContexts");
        var dbNames = new List<string>();
        foreach (var db in dbContexts.EnumerateArray())
        {
            dbNames.Add(db.GetProperty("dbContextName").GetString()!);
        }

        dbNames.Should().Contain("PublisherDbContext");
        dbNames.Should().Contain("ConsumerDbContext");
    }

    public async ValueTask DisposeAsync()
    {
        // Keep shared factory until test session tear-down
        await Task.CompletedTask;
    }
}
