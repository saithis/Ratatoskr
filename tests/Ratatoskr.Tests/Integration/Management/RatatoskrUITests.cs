using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;

namespace Ratatoskr.Tests.Integration.Management;

public class RatatoskrUITests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : ManagementTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task MapRatatoskrUI_ServesIndexHtml_Returns200OK()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync("/ratatoskr");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Ratatoskr Management Dashboard");
    }

    [Test]
    public async Task MapRatatoskrUI_ServesCssAndJsAssets_Returns200OK()
    {
        await StartManagementTestAsync();

        using var cssResponse = await HttpClient.GetAsync("/ratatoskr/app.css");
        cssResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var cssContent = await cssResponse.Content.ReadAsStringAsync();
        cssContent.Should().Contain("--bg-main");

        using var jsResponse = await HttpClient.GetAsync("/ratatoskr/app.js");
        jsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var jsContent = await jsResponse.Content.ReadAsStringAsync();
        jsContent.Should().Contain("Ratatoskr Dashboard");
    }

    [Test]
    public async Task MapRatatoskrUI_ConfigEndpoint_ReturnsValidConfiguration()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync("/ratatoskr/ui-api/config");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        config.GetProperty("title").GetString().Should().Be("Ratatoskr Dashboard");
        config.GetProperty("routePrefix").GetString().Should().Be("/ratatoskr");
        config.GetProperty("pollingIntervalMs").GetInt32().Should().Be(5000);
        config.GetProperty("enablePayloadEditing").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task RequeueOutbox_WithPayloadEdit_UpdatesContentInDatabase()
    {
        await StartManagementTestAsync();

        var messageId = await SeedPoisonedOutboxAsync();
        var requeueUrl =
            $"/ratatoskr/api/v1/efcore/contexts/TestDbContext/outbox/poisoned/{messageId}/requeue";

        const string newPayloadJson = "{\"modifiedField\":\"updatedValue\"}";
        using var requeueResponse = await HttpClient.PostAsJsonAsync(
            requeueUrl,
            new { payload = newPayloadJson }
        );
        requeueResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().SingleAsync(x => x.Id == messageId);

            entity.IsPoisoned.Should().BeFalse();
            entity.RequeuedCount.Should().Be(1);
            System.Text.Encoding.UTF8.GetString(entity.Content).Should().Be(newPayloadJson);
        });
    }

    [Test]
    public async Task CoreManagement_TopologyAndMetricsEndpoints_ReturnOk()
    {
        await StartManagementTestAsync();

        using var topologyResp = await HttpClient.GetAsync("/ratatoskr/api/v1/system/topology");
        topologyResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var topologyJson = await topologyResp.Content.ReadFromJsonAsync<JsonElement>();
        topologyJson.TryGetProperty("channels", out _).Should().BeTrue();

        using var metricsResp = await HttpClient.GetAsync("/ratatoskr/api/v1/system/metrics");
        metricsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var metricsJson = await metricsResp.Content.ReadFromJsonAsync<JsonElement>();
        metricsJson.GetProperty("instanceId").GetString().Should().NotBeNullOrEmpty();
    }
}
