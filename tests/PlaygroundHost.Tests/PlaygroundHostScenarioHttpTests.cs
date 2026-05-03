using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using PlaygroundHost.Tests.Fixtures;
using TUnit.Core;

namespace PlaygroundHost.Tests;

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

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        string testId,
        bool singleFlight,
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

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:rabbitmq", rabbit.ConnectionString);
            builder.UseSetting("ConnectionStrings:publisherdb", pubCs);
            builder.UseSetting("ConnectionStrings:consumerdb", conCs);
            builder.UseSetting("ConnectionStrings:playgrounddb", playCs);
            builder.UseSetting("Playground:Enabled", "true");
            builder.UseSetting("Playground:SingleFlight", singleFlight ? "true" : "false");
            builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
            if (extraSettings is not null)
            {
                foreach (var kv in extraSettings)
                    builder.UseSetting(kv.Key, kv.Value);
            }
        });
    }

    [Test]
    public async Task OutboxSuccessScenario_ReturnsPassed()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId, singleFlight: false);
        var client = factory.CreateClient();

        var catalog = await client.GetFromJsonAsync<List<ScenarioCatalogDto>>("/api/playground/scenarios");
        catalog.Should().NotBeNull();
        catalog!.Should().Contain(s => s.slug == "outbox-success");

        var runRes = await client.PostAsync("/api/playground/scenarios/outbox-success/run", null);
        runRes.StatusCode.Should().BeOneOf(HttpStatusCode.Accepted, HttpStatusCode.OK);
        var runBody = await runRes.Content.ReadFromJsonAsync<RunAcceptedDto>();
        runBody!.runId.Should().NotBeEmpty();

        var deadline = DateTime.UtcNow.AddSeconds(90);
        ScenarioRunStatusDto? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetFromJsonAsync<ScenarioRunStatusDto>($"/api/playground/runs/{runBody.runId}");
            if (status is { state: "Passed" or "Failed" })
                break;
            await Task.Delay(500);
        }

        status.Should().NotBeNull();
        status!.state.Should().Be("Passed", $"detail: {status.detail}");
    }

    [Test]
    public async Task SingleFlight_SecondConcurrentStart_ReturnsBadRequest()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(testId, singleFlight: true);
        var client = factory.CreateClient();

        var first = client.PostAsync("/api/playground/scenarios/outbox-success/run", null);
        var second = client.PostAsync("/api/playground/scenarios/outbox-success/run", null);
        var responses = await Task.WhenAll(first, second);

        var accepted = responses.Count(r => r.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest);
        accepted.Should().Be(1);
        rejected.Should().Be(1);
    }

    [Test]
    public async Task CancelSmokeScenario_AfterCancel_DetailIsCancelled()
    {
        var testId = Guid.NewGuid().ToString("N");
        await using var factory = await CreateFactoryAsync(
            testId,
            singleFlight: false,
            extraSettings: new Dictionary<string, string> { ["Playground:RegisterCancelSmokeScenario"] = "1" });
        var client = factory.CreateClient();

        var runRes = await client.PostAsync("/api/playground/scenarios/cancel-smoke/run", null);
        runRes.EnsureSuccessStatusCode();
        var runBody = await runRes.Content.ReadFromJsonAsync<RunAcceptedDto>();
        var runId = runBody!.runId;

        var cancelRes = await client.PostAsync($"/api/playground/runs/{runId}/cancel", null);
        cancelRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        ScenarioRunStatusDto? status = null;
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetFromJsonAsync<ScenarioRunStatusDto>($"/api/playground/runs/{runId}");
            if (status is { state: "Failed" })
                break;
            await Task.Delay(300);
        }

        status.Should().NotBeNull();
        status!.state.Should().Be("Failed");
        status.detail.Should().Be("Cancelled.");
    }

    private sealed record ScenarioCatalogDto(string slug, string title, string description, string? topic);

    private sealed record RunAcceptedDto(Guid runId, string? title);

    private sealed record ScenarioRunStatusDto(Guid id, string scenarioSlug, string state, DateTimeOffset startedAt, DateTimeOffset? completedAt, string? detail);
}
