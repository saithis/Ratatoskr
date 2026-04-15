using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
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

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("dbContexts", out var dbContexts).Should().BeTrue();
        dbContexts.GetArrayLength().Should().BeGreaterThan(0);

        var first = dbContexts.EnumerateArray().First();
        first.GetProperty("dbContextName").GetString().Should().Be("TestDbContext");
    }

    [Test]
    public async Task Health_LastProcessedAt_ReflectsProcessorState()
    {
        await StartManagementTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var first = body.GetProperty("dbContexts").EnumerateArray().First();

        // LastOutboxProcessedAt and LastInboxProcessedAt should be present (may be null if processor not running)
        first.TryGetProperty("lastOutboxProcessedAt", out _).Should().BeTrue();
        first.TryGetProperty("lastInboxProcessedAt", out _).Should().BeTrue();
    }
}
