using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using PlaygroundHost;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Examples;

[ClassDataSource<RabbitMqContainerFixture, PostgresContainerFixture>(Shared = [SharedType.PerTestSession, SharedType.PerTestSession])]
public sealed class PlaygroundHostScenarioHttpTests(RabbitMqContainerFixture rabbit, PostgresContainerFixture postgres)
{
    private static async Task CreateDatabaseAsync(string maintenanceConnectionString, string databaseName)
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

    private async Task<WebApplicationFactory<PlaygroundHostAppMarker>> CreateFactoryAsync(
        string testId,
        IReadOnlyDictionary<string, string>? extraSettings = null)
    {
        var pubDb = $"ph_{testId}_pub";
        var conDb = $"ph_{testId}_con";
        var playDb = $"ph_{testId}_play";
        var maint = MaintenanceConnectionString(postgres.ConnectionString);
        await CreateDatabaseAsync(maint, pubDb);
        await CreateDatabaseAsync(maint, conDb);
        await CreateDatabaseAsync(maint, playDb);

        var pubCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = pubDb }.ToString();
        var conCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = conDb }.ToString();
        var playCs = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = playDb }.ToString();

        return new WebApplicationFactory<PlaygroundHostAppMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
            builder.UseSetting("ConnectionStrings:publisherdb", pubCs);
            builder.UseSetting("ConnectionStrings:consumerdb", conCs);
            builder.UseSetting("ConnectionStrings:playgrounddb", playCs);
            builder.UseSetting("Playground:Enabled", "true");
            builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
            if (extraSettings is not null)
            {
                foreach (var kv in extraSettings)
                    builder.UseSetting(kv.Key, kv.Value);
            }
        });
    }

    private static async Task<ScenarioRunStatusDto> WaitForTerminalAsync(
        HttpClient client,
        Guid runId,
        int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        ScenarioRunStatusDto? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetFromJsonAsync<ScenarioRunStatusDto>($"/api/playground/runs/{runId}");
            if (status is { state: "Passed" or "Failed" or "Cancelled" })
                break;
            await Task.Delay(250);
        }

        status.Should().NotBeNull();
        return status!;
    }

    private static async Task<Guid> StartScenarioAsync(HttpClient client, string slug, bool confirmDanger = false)
    {
        var q = confirmDanger ? "?confirmDanger=true" : "";
        var runRes = await client.PostAsync($"/api/playground/scenarios/{Uri.EscapeDataString(slug)}/run{q}", null);
        var errBody = await runRes.Content.ReadAsStringAsync();
        var okStart = runRes.StatusCode == HttpStatusCode.Accepted || runRes.StatusCode == HttpStatusCode.OK;
        okStart.Should().BeTrue($"POST run failed for slug={slug}: {(int)runRes.StatusCode} {errBody}");
        var runBody = await runRes.Content.ReadFromJsonAsync<RunAcceptedDto>();
        runBody!.runId.Should().NotBeEmpty();
        return runBody.runId;
    }

    [Test]
    public async Task Catalog_Contains_AllScenarios()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId);
        var client = factory.CreateClient();

        var catalog = await client.GetFromJsonAsync<List<ScenarioCatalogDto>>("/api/playground/scenarios");
        catalog.Should().NotBeNull();
        var slugs = catalog!.Select(c => c.slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        slugs.Should().Contain("outbox-success");
        slugs.Should().Contain("cancel-smoke");
        slugs.Should().Contain("blocking-hold");
    }

    [Test]
    public async Task ConcurrentStarts_TwoOutboxSuccessRuns_BothAccepted()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId);
        var client = factory.CreateClient();

        var a = client.PostAsync("/api/playground/scenarios/outbox-success/run", null);
        var b = client.PostAsync("/api/playground/scenarios/outbox-success/run", null);
        var responses = await Task.WhenAll(a, b);

        responses.Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.Accepted || r.StatusCode == HttpStatusCode.OK);
    }

    [Test]
    public async Task CancelSmoke_AfterCancel_CompletesWithPassAndCancelledDetail()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId);
        var client = factory.CreateClient();

        var runId = await StartScenarioAsync(client, "cancel-smoke");
        var cancelRes = await client.PostAsync($"/api/playground/runs/{runId}/cancel", null);
        cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await WaitForTerminalAsync(client, runId, 30);
        status.state.Should().Be("Passed");
        status.detail.Should().Contain("Cancelled", $"detail was: {status.detail}");
    }

    [Test]
    public async Task BlockingHold_WithCancel_TerminatesAsCancelled()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId);
        var client = factory.CreateClient();

        var runId = await StartScenarioAsync(client, "blocking-hold", confirmDanger: true);
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
    [Arguments("business-rejection")]
    [Arguments("direct-consume-success")]
    [Arguments("direct-consume-retry")]
    [Arguments("direct-consume-dlq")]
    [Arguments("fanout-two-handlers-on-orderplaced")]
    [Arguments("efcore-internal-command")]
    [Arguments("replay-dedups")]
    public async Task Scenario_EndsPassed(string slug)
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId);
        var client = factory.CreateClient();

        var runId = await StartScenarioAsync(client, slug);
        var status = await WaitForTerminalAsync(client, runId, 120);
        status.state.Should().Be("Passed", $"slug={slug} detail={status.detail}");
    }

    [Test]
    public async Task BlockingHold_WithoutDangerConfirm_ReturnsBadRequest()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId);
        var client = factory.CreateClient();

        var runRes = await client.PostAsync("/api/playground/scenarios/blocking-hold/run", null);
        runRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record ScenarioCatalogDto(string slug, string title, string description, string? topic);

    private sealed record RunAcceptedDto(Guid runId, string? title);

    private sealed record ScenarioRunStatusDto(Guid id, string scenarioSlug, string state, DateTimeOffset startedAt, DateTimeOffset? completedAt, string? detail);
}
