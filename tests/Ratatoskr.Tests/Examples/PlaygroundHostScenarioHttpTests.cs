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
public sealed class PlaygroundHostScenarioHttpTests : IAsyncDisposable
{
    private static readonly SemaphoreSlim _factoryLock = new(1, 1);
    private static int _sharedFactoryUsers;
    private static WebApplicationFactory<PlaygroundHostAppMarker>? _sharedFactory;

    private readonly RabbitMqContainerFixture _rabbit;
    private readonly PostgresContainerFixture _postgres;

    public PlaygroundHostScenarioHttpTests(
        RabbitMqContainerFixture rabbit,
        PostgresContainerFixture postgres
    )
    {
        _rabbit = rabbit;
        _postgres = postgres;
        Interlocked.Increment(ref _sharedFactoryUsers);
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

    private static string MaintenanceConnectionString(string fixtureCs)
    {
        var b = new NpgsqlConnectionStringBuilder(fixtureCs) { Database = "postgres" };
        return b.ToString();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "WebApplicationFactory ownership is transferred to _sharedFactory and disposed in DisposeAsync."
    )]
    private async Task<
        WebApplicationFactory<PlaygroundHostAppMarker>
    > GetOrCreateSharedFactoryAsync()
    {
        var existing = Volatile.Read(ref _sharedFactory);
        if (existing is not null)
        {
            return existing;
        }

        await _factoryLock.WaitAsync();
        try
        {
            existing = Volatile.Read(ref _sharedFactory);
            if (existing is not null)
            {
                return existing;
            }

            const string testId = "shared";
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

            // WebApplicationFactory.EnsureServer is not safe against concurrent first access; parallel tests can
            // otherwise both start the deferred host and race EnsureCreated on the same databases (42P07).
            _ = factory.Server;

            Volatile.Write(ref _sharedFactory, factory);
            return factory;
        }
        finally
        {
            _factoryLock.Release();
        }
    }

    private async Task<HttpClient> GetClientAsync() =>
        (await GetOrCreateSharedFactoryAsync()).CreateClient();

    private async Task RunHttpTestAsync(Func<HttpClient, Task> test)
    {
        using var client = await GetClientAsync();
        await test(client);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "IDISP007:Don't dispose injected",
        Justification = "_sharedFactory is a session-scoped static resource owned by this test class, not DI-injected."
    )]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Decrement(ref _sharedFactoryUsers) != 0)
        {
            return;
        }

        await _factoryLock.WaitAsync();
        try
        {
            var factory = Interlocked.Exchange(ref _sharedFactory, value: null);
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }
        }
        finally
        {
            _factoryLock.Release();
        }
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
            if (status is { State: "Passed" or "Failed" or "Cancelled" })
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
        using var runRes = await client.PostAsync(
            $"/api/playground/scenarios/{Uri.EscapeDataString(slug)}/run{q}",
            content: null
        );
        var errBody = await runRes.Content.ReadAsStringAsync();
        var okStart = runRes.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK;
        okStart
            .Should()
            .BeTrue($"POST run failed for slug={slug}: {(int)runRes.StatusCode} {errBody}");
        var runBody = await runRes.Content.ReadFromJsonAsync<RunAcceptedDto>();
        runBody!.RunId.Should().NotBeEmpty();
        return runBody.RunId;
    }

    [Test]
    public Task Catalog_Contains_AllScenarios() =>
        RunHttpTestAsync(async client =>
        {
            var catalog = await client.GetFromJsonAsync<List<ScenarioCatalogDto>>(
                "/api/playground/scenarios"
            );
            catalog.Should().NotBeNull();
            var slugs = catalog.Select(c => c.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            slugs.Should().Contain("outbox-success");
            slugs.Should().Contain("cancel-smoke");
            slugs.Should().Contain("blocking-hold");
        });

    [Test]
    [Retry(2)]
    public Task ConcurrentRuns_TwoOutboxSuccessRuns_BothPass() =>
        RunHttpTestAsync(async client =>
        {
            var startA = client.PostAsync(
                "/api/playground/scenarios/outbox-success/run",
                content: null
            );
            var startB = client.PostAsync(
                "/api/playground/scenarios/outbox-success/run",
                content: null
            );
            using var runA = await startA;
            using var runB = await startB;

            runA.StatusCode.Should().BeOneOf(HttpStatusCode.Accepted, HttpStatusCode.OK);
            runB.StatusCode.Should().BeOneOf(HttpStatusCode.Accepted, HttpStatusCode.OK);

            var idA = (await runA.Content.ReadFromJsonAsync<RunAcceptedDto>())!.RunId;
            var idB = (await runB.Content.ReadFromJsonAsync<RunAcceptedDto>())!.RunId;
            idA.Should().NotBe(idB);

            var statusA = await WaitForTerminalAsync(client, idA, 30);
            var statusB = await WaitForTerminalAsync(client, idB, 30);
            statusA.State.Should().Be("Passed", statusA.Detail);
            statusB.State.Should().Be("Passed", statusB.Detail);
        });

    [Test]
    public Task CancelSmoke_AfterCancel_CompletesWithPassAndCancelledDetail() =>
        RunHttpTestAsync(async client =>
        {
            const string slug = "cancel-smoke";

            var runId = await StartScenarioAsync(client, slug);
            using var cancelRes = await client.PostAsync(
                $"/api/playground/runs/{runId}/cancel",
                content: null
            );
            cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);

            var status = await WaitForTerminalAsync(client, runId, 30);
            status.State.Should().Be("Passed");
            status.Detail.Should().Contain("Cancelled", $"detail was: {status.Detail}");
        });

    [Test]
    public Task BlockingHold_WithCancel_TerminatesAsCancelled() =>
        RunHttpTestAsync(async client =>
        {
            const string slug = "blocking-hold";

            var runId = await StartScenarioAsync(client, slug, confirmDanger: true);
            using var cancelRes = await client.PostAsync(
                $"/api/playground/runs/{runId}/cancel",
                content: null
            );
            cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);

            var status = await WaitForTerminalAsync(client, runId, 30);
            status.State.Should().Be("Cancelled");
        });

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
    [Retry(2)]
    public Task Scenario_EndsPassed(string slug) =>
        RunHttpTestAsync(async client =>
        {
            var runId = await StartScenarioAsync(client, slug);
            var status = await WaitForTerminalAsync(client, runId, 20);
            status.State.Should().Be("Passed", $"slug={slug} detail={status.Detail}");
        });

    [Test]
    public Task BlockingHold_WithoutDangerConfirm_ReturnsBadRequest() =>
        RunHttpTestAsync(async client =>
        {
            const string slug = "blocking-hold";

            using var runRes = await client.PostAsync(
                $"/api/playground/scenarios/{slug}/run",
                content: null
            );
            runRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        });

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "WebApplicationFactory is disposed via await using for this isolated host configuration."
    )]
    [Test]
    public async Task BlockingHold_WithConfiguredRunTimeout_TerminatesAsTimedOut()
    {
        var testId = Guid.NewGuid().ToString("N")[..12];
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

        await using var factory =
            new WebApplicationFactory<PlaygroundHostAppMarker>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:rabbitmq", _rabbit.ConnectionString);
                builder.UseSetting("ConnectionStrings:publisherdb", pubCs);
                builder.UseSetting("ConnectionStrings:consumerdb", conCs);
                builder.UseSetting("ConnectionStrings:playgrounddb", playCs);
                builder.UseSetting("Playground:Enabled", "true");
                builder.UseSetting("Playground:RunTimeoutSeconds", "8");
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
            });
        _ = factory.Server;

        using var client = factory.CreateClient();
        var runId = await StartScenarioAsync(client, "blocking-hold", confirmDanger: true);
        var status = await WaitForTerminalAsync(client, runId, 20);
        status.State.Should().Be("Failed");
        status.Detail.Should().Contain("Timed out", $"detail was: {status.Detail}");
    }

    private sealed record ScenarioCatalogDto(
        string Slug,
        string Title,
        string Description,
        string? Topic
    );

    private sealed record RunAcceptedDto(Guid RunId, string? Title);

    private sealed record ScenarioRunStatusDto(
        Guid Id,
        string ScenarioSlug,
        string State,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        string? Detail
    );
}
