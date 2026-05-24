using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using PlaygroundHost;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Examples;

[ClassDataSource<RabbitMqContainerFixture, PostgresContainerFixture>(
    Shared = [SharedType.PerTestSession, SharedType.PerTestSession]
)]
public sealed class PlaygroundHostScenarioHttpTests(
    RabbitMqContainerFixture rabbit,
    PostgresContainerFixture postgres
) : IAsyncDisposable
{
    private static WebApplicationFactory<PlaygroundHostAppMarker>? _sharedFactory;
    private static readonly SemaphoreSlim _factoryLock = new(1, 1);

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

    private static string MaintenanceConnectionString(string fixtureCs)
    {
        var b = new NpgsqlConnectionStringBuilder(fixtureCs) { Database = "postgres" };
        return b.ToString();
    }

    private async Task<
        WebApplicationFactory<PlaygroundHostAppMarker>
    > GetOrCreateSharedFactoryAsync()
    {
        if (_sharedFactory is not null)
        {
            return _sharedFactory;
        }

        await _factoryLock.WaitAsync();
        try
        {
            if (_sharedFactory is not null)
            {
                return _sharedFactory;
            }

            var testId = "shared";
            var pubDb = $"ph_{testId}_pub";
            var conDb = $"ph_{testId}_con";
            var playDb = $"ph_{testId}_play";
            var maint = MaintenanceConnectionString(postgres.ConnectionString);
            await CreateDatabaseAsync(maint, pubDb);
            await CreateDatabaseAsync(maint, conDb);
            await CreateDatabaseAsync(maint, playDb);

            var pubCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                Database = pubDb,
            }.ToString();
            var conCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                Database = conDb,
            }.ToString();
            var playCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                Database = playDb,
            }.ToString();

            _sharedFactory =
                new WebApplicationFactory<PlaygroundHostAppMarker>().WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
                    builder.UseSetting("ConnectionStrings:publisherdb", pubCs);
                    builder.UseSetting("ConnectionStrings:consumerdb", conCs);
                    builder.UseSetting("ConnectionStrings:playgrounddb", playCs);
                    builder.UseSetting("Playground:Enabled", "true");
                    builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
                });

            // WebApplicationFactory.EnsureServer is not safe against concurrent first access; parallel tests can
            // otherwise both start the deferred host and race EnsureCreated on the same databases (42P07).
            _ = _sharedFactory.Server;

            return _sharedFactory;
        }
        finally
        {
            _factoryLock.Release();
        }
    }

    private async Task<HttpClient> GetClientAsync() =>
        (await GetOrCreateSharedFactoryAsync()).CreateClient();

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static async Task<ScenarioRunStatusDto> WaitForTerminalAsync(
        HttpClient client,
        Guid runId,
        int timeoutSeconds
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        ScenarioRunStatusDto? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetFromJsonAsync<ScenarioRunStatusDto>(
                $"/api/playground/runs/{runId}"
            );
            if (status is { state: "Passed" or "Failed" or "Cancelled" })
            {
                break;
            }

            await Task.Delay(250);
        }

        status.Should().NotBeNull();
        return status;
    }

    private static async Task<Guid> StartScenarioAsync(
        HttpClient client,
        string slug,
        bool confirmDanger = false
    )
    {
        var q = confirmDanger ? "?confirmDanger=true" : "";
        var runRes = await client.PostAsync(
            $"/api/playground/scenarios/{Uri.EscapeDataString(slug)}/run{q}",
            null
        );
        var errBody = await runRes.Content.ReadAsStringAsync();
        var okStart = runRes.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK;
        okStart
            .Should()
            .BeTrue($"POST run failed for slug={slug}: {(int)runRes.StatusCode} {errBody}");
        var runBody = await runRes.Content.ReadFromJsonAsync<RunAcceptedDto>();
        runBody!.runId.Should().NotBeEmpty();
        return runBody.runId;
    }

    [Test]
    public async Task Catalog_Contains_AllScenarios()
    {
        var client = await GetClientAsync();

        var catalog = await client.GetFromJsonAsync<List<ScenarioCatalogDto>>(
            "/api/playground/scenarios"
        );
        catalog.Should().NotBeNull();
        var slugs = catalog.Select(c => c.slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        slugs.Should().Contain("outbox-success");
        slugs.Should().Contain("cancel-smoke");
        slugs.Should().Contain("blocking-hold");
    }

    [Test]
    public async Task ConcurrentStarts_TwoOutboxSuccessRuns_BothAccepted()
    {
        var client = await GetClientAsync();

        var a = client.PostAsync("/api/playground/scenarios/outbox-success/run", null);
        var b = client.PostAsync("/api/playground/scenarios/outbox-success/run", null);
        var responses = await Task.WhenAll(a, b);

        responses
            .Should()
            .OnlyContain(r =>
                r.StatusCode == HttpStatusCode.Accepted || r.StatusCode == HttpStatusCode.OK
            );
    }

    [Test]
    public async Task CancelSmoke_AfterCancel_CompletesWithPassAndCancelledDetail()
    {
        var slug = "cancel-smoke";
        var client = await GetClientAsync();

        var runId = await StartScenarioAsync(client, slug);
        var cancelRes = await client.PostAsync($"/api/playground/runs/{runId}/cancel", null);
        cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await WaitForTerminalAsync(client, runId, 30);
        status.state.Should().Be("Passed");
        status.detail.Should().Contain("Cancelled", $"detail was: {status.detail}");
    }

    [Test]
    public async Task BlockingHold_WithCancel_TerminatesAsCancelled()
    {
        var slug = "blocking-hold";
        var client = await GetClientAsync();

        var runId = await StartScenarioAsync(client, slug, confirmDanger: true);
        var cancelRes = await client.PostAsync($"/api/playground/runs/{runId}/cancel", null);
        cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await WaitForTerminalAsync(client, runId, 30);
        status.state.Should().Be("Cancelled");
    }

    [Test]
    [Arguments("outbox-success")]
    [Arguments("outbox-retry-then-success")]
    [Arguments("outbox-poison")]
    [Arguments("oversized-payload-rolls-back")]
    [Arguments("inbox-retry-then-success")]
    [Arguments("inbox-poison")]
    [Arguments("inbox-dedups")]
    [Arguments("business-rejection")]
    [Arguments("direct-consume-success")]
    [Arguments("direct-consume-retry")]
    [Arguments("direct-consume-dlq")]
    [Arguments("fanout-two-handlers-on-orderplaced")]
    [Arguments("efcore-internal-command")]
    public async Task Scenario_EndsPassed(string slug)
    {
        var client = await GetClientAsync();

        var runId = await StartScenarioAsync(client, slug);
        var status = await WaitForTerminalAsync(client, runId, 20);
        status.state.Should().Be("Passed", $"slug={slug} detail={status.detail}");
    }

    [Test]
    public async Task BlockingHold_WithoutDangerConfirm_ReturnsBadRequest()
    {
        var slug = "blocking-hold";
        var client = await GetClientAsync();

        var runRes = await client.PostAsync($"/api/playground/scenarios/{slug}/run", null);
        runRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task BlockingHold_WithConfiguredRunTimeout_TerminatesAsTimedOut()
    {
        var testId = Guid.NewGuid().ToString("N")[..12];
        var pubDb = $"ph_{testId}_pub";
        var conDb = $"ph_{testId}_con";
        var playDb = $"ph_{testId}_play";
        var maint = MaintenanceConnectionString(postgres.ConnectionString);
        await CreateDatabaseAsync(maint, pubDb);
        await CreateDatabaseAsync(maint, conDb);
        await CreateDatabaseAsync(maint, playDb);

        var pubCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = pubDb,
        }.ToString();
        var conCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = conDb,
        }.ToString();
        var playCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = playDb,
        }.ToString();

        await using var factory =
            new WebApplicationFactory<PlaygroundHostAppMarker>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
                builder.UseSetting("ConnectionStrings:publisherdb", pubCs);
                builder.UseSetting("ConnectionStrings:consumerdb", conCs);
                builder.UseSetting("ConnectionStrings:playgrounddb", playCs);
                builder.UseSetting("Playground:Enabled", "true");
                builder.UseSetting("Playground:RunTimeoutSeconds", "8");
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
            });
        _ = factory.Server;

        var client = factory.CreateClient();
        var runId = await StartScenarioAsync(client, "blocking-hold", confirmDanger: true);
        var status = await WaitForTerminalAsync(client, runId, 20);
        status.state.Should().Be("Failed");
        status.detail.Should().Contain("Timed out", $"detail was: {status.detail}");
    }

    private sealed record ScenarioCatalogDto(
        string slug,
        string title,
        string description,
        string? topic
    );

    private sealed record RunAcceptedDto(Guid runId, string? title);

    private sealed record ScenarioRunStatusDto(
        Guid id,
        string scenarioSlug,
        string state,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        string? detail
    );
}
