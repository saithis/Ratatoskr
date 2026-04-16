using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class HealthEndpointTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : ManagementTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task Health_ReturnsCachedCounts()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();
        await SeedPoisonedInboxAsync();
        await RefreshMetricsAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/contexts/TestDbContext/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dbContextName").GetString().Should().Be("TestDbContext");
        body.GetProperty("poisonedOutboxCount").GetInt64().Should().Be(1,
            "the seeded outbox entity is poisoned and the metrics scrape has just run");
        body.GetProperty("poisonedInboxCount").GetInt64().Should().Be(1,
            "the seeded inbox handler is poisoned and the metrics scrape has just run");
        body.GetProperty("pendingOutboxCount").GetInt64().Should().Be(0);
        body.GetProperty("pendingInboxCount").GetInt64().Should().Be(0);
    }

    [Test]
    public async Task Health_LastProcessedAt_IsPopulatedOnceProcessorsRun()
    {
        await StartManagementTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/contexts/TestDbContext/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Processors initialise `LastSuccessfulProcessingAt` from TimeProvider at construction, so
        // the field is always populated once hosted services have started. This is what the UI
        // actually renders — asserting the shape is populated is what keeps the contract honest.
        body.GetProperty("lastOutboxProcessedAt").ValueKind.Should().Be(JsonValueKind.String,
            "the outbox processor has started and stamped LastSuccessfulProcessingAt");
        body.GetProperty("lastInboxProcessedAt").ValueKind.Should().Be(JsonValueKind.String,
            "the inbox processor has started and stamped LastSuccessfulProcessingAt");
    }

    [Test]
    public async Task Contexts_ReturnsListOfRegisteredDbContexts()
    {
        await StartManagementTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/contexts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("contexts", out var contexts).Should().BeTrue();
        contexts.GetArrayLength().Should().BeGreaterThan(0);

        var first = contexts.EnumerateArray().First();
        first.GetProperty("name").GetString().Should().Be("TestDbContext");
        first.GetProperty("hasOutbox").GetBoolean().Should().BeTrue();
        first.GetProperty("hasInbox").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task Health_UnknownContext_Returns404()
    {
        await StartManagementTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/contexts/NonExistentContext/health");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The backlog metrics are scraped by a background service on a 30s cadence, which is far too
    /// slow for tests. Rather than waiting or hacking the interval down, we synchronously drive one
    /// scrape of the same code path by resolving the hosted service and calling
    /// <see cref="EfCoreMetricsBackgroundService{TDbContext}.UpdateMetricsAsync"/> directly —
    /// exactly what the timer loop would do.
    /// </summary>
    private async Task RefreshMetricsAsync()
    {
        // The BGS is only registered under the IHostedService contract (see AddHostedService<T>),
        // so we fish it out by type from the enumerable.
        var bg = Services.GetServices<IHostedService>()
            .OfType<EfCoreMetricsBackgroundService<TestDbContext>>()
            .Single();
        await bg.UpdateMetricsAsync(CancellationToken.None);
    }
}
