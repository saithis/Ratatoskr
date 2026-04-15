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

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/contexts/TestDbContext/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dbContextName").GetString().Should().Be("TestDbContext");
    }

    [Test]
    public async Task Health_LastProcessedAt_ReflectsProcessorState()
    {
        await StartManagementTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/contexts/TestDbContext/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("lastOutboxProcessedAt", out _).Should().BeTrue();
        body.TryGetProperty("lastInboxProcessedAt", out _).Should().BeTrue();
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
        first.TryGetProperty("hasOutbox", out _).Should().BeTrue();
        first.TryGetProperty("hasInbox", out _).Should().BeTrue();
    }

    [Test]
    public async Task Health_UnknownContext_Returns404()
    {
        await StartManagementTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/contexts/NonExistentContext/health");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
