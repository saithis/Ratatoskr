using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    public async Task MapRatatoskrUI_IndexHtml_ReferencesAssetsByAbsolutePath()
    {
        await StartManagementTestAsync();

        // Opened without a trailing slash the browser resolves relative asset URLs against
        // "/", so the served markup has to point at the mount point absolutely.
        using var response = await HttpClient.GetAsync("/ratatoskr");
        var html = await response.Content.ReadAsStringAsync();

        html.Should().NotContain("__RATATOSKR_BASE__");
        html.Should().Contain("href=\"/ratatoskr/app.css\"");
        html.Should().Contain("src=\"/ratatoskr/app.js\"");
        html.Should().Contain("window.RATATOSKR_BASE_PATH = \"/ratatoskr\"");
    }

    [Test]
    public async Task MapRatatoskrUI_WithTrailingSlash_ServesTheSameIndexHtml()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync("/ratatoskr/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("src=\"/ratatoskr/app.js\"");
    }

    [Test]
    public async Task MapRatatoskrUI_BehindPathBase_RootsAssetsAndApiPathsAtPathBase()
    {
        await StartManagementTestAsync(services =>
            services.AddSingleton<IStartupFilter>(new PathBaseStartupFilter("/proxy"))
        );

        using var response = await HttpClient.GetAsync("/proxy/ratatoskr");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("href=\"/proxy/ratatoskr/app.css\"");
        html.Should().Contain("src=\"/proxy/ratatoskr/app.js\"");

        using var cssResponse = await HttpClient.GetAsync("/proxy/ratatoskr/app.css");
        cssResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var configResponse = await HttpClient.GetAsync("/proxy/ratatoskr/ui-api/config");
        var config = await configResponse.Content.ReadFromJsonAsync<JsonElement>();
        config.GetProperty("routePrefix").GetString().Should().Be("/proxy/ratatoskr");
        config.GetProperty("defaultBasePath").GetString().Should().Be("/proxy/ratatoskr/api/v1");
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
        config.GetProperty("defaultBasePath").GetString().Should().Be("/ratatoskr/api/v1");
    }

    [Test]
    public async Task ContextsEndpoint_ReturnsNameUsableAsPoisonRouteSegment()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync();

        using var contextsResponse = await HttpClient.GetAsync("/ratatoskr/api/v1/efcore/contexts");
        contextsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var contexts = await contextsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var first = contexts.GetProperty("contexts")[0];
        var contextName = first.GetProperty("name").GetString();
        contextName.Should().Be(nameof(TestDbContext));
        first.GetProperty("hasOutbox").GetBoolean().Should().BeTrue();
        first.GetProperty("hasInbox").GetBoolean().Should().BeTrue();

        // The workbench builds its URLs straight from the entry, so the name has to be a
        // usable route segment on its own rather than a nested object the client must unwrap.
        using var poisonedResponse = await HttpClient.GetAsync(
            $"/ratatoskr/api/v1/efcore/contexts/{contextName}/outbox/poisoned"
        );
        poisonedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var poisoned = await poisonedResponse.Content.ReadFromJsonAsync<JsonElement>();
        poisoned.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        poisoned.GetProperty("items")[0].TryGetProperty("id", out _).Should().BeTrue();
    }

    [Test]
    public async Task InboxPoisonedList_ExposesHandlerStatusIdUsableForRowActions()
    {
        await StartManagementTestAsync();
        var (_, handlerStatusId) = await SeedPoisonedInboxAsync();

        using var listResponse = await HttpClient.GetAsync(
            "/ratatoskr/api/v1/efcore/contexts/TestDbContext/inbox/poisoned"
        );
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var row = list.GetProperty("items")[0];
        var rowId = row.GetProperty("handlerStatusId").GetGuid();
        rowId.Should().Be(handlerStatusId);

        // Inbox rows are keyed by handler status, not by message id: the per-row Inspect,
        // Requeue and Delete actions all address that id.
        using var detailResponse = await HttpClient.GetAsync(
            $"/ratatoskr/api/v1/efcore/contexts/TestDbContext/inbox/poisoned/{rowId}"
        );
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
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

    /// <summary>
    /// Simulates hosting behind a reverse proxy sub-path. A startup filter is the only way to
    /// insert middleware ahead of the test host's pipeline from a test's service configuration.
    /// </summary>
    private sealed class PathBaseStartupFilter(string pathBase) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UsePathBase(pathBase);
                next(app);
            };
        }
    }
}
