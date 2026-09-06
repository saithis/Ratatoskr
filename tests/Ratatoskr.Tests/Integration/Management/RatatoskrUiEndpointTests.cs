using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Management;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;
using TUnit.Core;

namespace Ratatoskr.Tests.Integration.Management;

public class RatatoskrUiEndpointTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private const string UiBasePath = "/ratatoskr";

    [Test]
    public async Task StaticAssets_IndexHtmlAndCssAndJs_ServedSuccessfully()
    {
        await StartManagementTestAsync(services =>
        {
            services.AddRatatoskrManagement(o =>
            {
                o.ServiceName = "web-svc";
                o.EnableHeartbeat = false;
            });
            services.AddRatatoskrUI();
        });

        // 1. Root HTML
        using var indexResp = await HttpClient.GetAsync($"{UiBasePath}/");
        indexResp.StatusCode.Should().Be(HttpStatusCode.OK);
        indexResp.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        var html = await indexResp.Content.ReadAsStringAsync();
        html.Should().Contain("Ratatoskr Management");
        html.Should().Contain("<div class=\"layout\">");

        // 2. CSS
        using var cssResp = await HttpClient.GetAsync($"{UiBasePath}/css/dashboard.css");
        cssResp.StatusCode.Should().Be(HttpStatusCode.OK);
        cssResp.Content.Headers.ContentType?.MediaType.Should().Be("text/css");

        // 3. JS
        using var jsResp = await HttpClient.GetAsync($"{UiBasePath}/js/app.js");
        jsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        jsResp.Content.Headers.ContentType?.MediaType.Should().Contain("javascript");
    }

    [Test]
    public async Task Api_ServicesAndOutboxEndpoints_ReturnExpectedData()
    {
        await StartManagementTestAsync(services =>
        {
            services.AddRatatoskrManagement(o =>
            {
                o.ServiceName = "portal-service";
                o.EnableHeartbeat = false;
            });
            services.AddRatatoskrUI();
        });

        var outboxId = await SeedPoisonedOutboxAsync("portal.user-registered");

        // 1. GET /ratatoskr/api/services
        using var servicesResp = await HttpClient.GetAsync($"{UiBasePath}/api/services");
        servicesResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var servicesJson = await servicesResp.Content.ReadFromJsonAsync<JsonElement>();
        servicesJson.ValueKind.Should().Be(JsonValueKind.Array);
        servicesJson.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // 2. GET /ratatoskr/api/services/portal-service
        using var detailResp = await HttpClient.GetAsync($"{UiBasePath}/api/services/portal-service");
        detailResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var detailJson = await detailResp.Content.ReadFromJsonAsync<JsonElement>();
        detailJson.GetProperty("serviceName").GetString().Should().Be("portal-service");
        detailJson.GetProperty("dbContexts").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        // 3. GET /ratatoskr/api/services/portal-service/contexts/TestDbContext/outbox
        using var outboxResp = await HttpClient.GetAsync(
            $"{UiBasePath}/api/services/portal-service/contexts/TestDbContext/outbox?status=Poisoned"
        );
        outboxResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var outboxJson = await outboxResp.Content.ReadFromJsonAsync<JsonElement>();
        outboxJson.GetProperty("totalCount").GetInt32().Should().Be(1);

        var items = outboxJson.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("id").GetGuid().Should().Be(outboxId);

        // 4. POST /ratatoskr/api/services/portal-service/contexts/TestDbContext/outbox/{id}/requeue
        using var requeueResp = await HttpClient.PostAsync(
            $"{UiBasePath}/api/services/portal-service/contexts/TestDbContext/outbox/{outboxId}/requeue",
            content: null
        );
        requeueResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var requeueJson = await requeueResp.Content.ReadFromJsonAsync<RequeueResultDto>();
        requeueJson.Should().NotBeNull();
        requeueJson!.RequeuedCount.Should().Be(1);
    }

    [Test]
    public async Task ServerSentEvents_Endpoint_ConnectsAndStreamsSnapshot()
    {
        await StartManagementTestAsync(services =>
        {
            services.AddRatatoskrManagement(o =>
            {
                o.ServiceName = "sse-service";
                o.EnableHeartbeat = false;
            });
            services.AddRatatoskrUI();
        });

        // Ensure registry is populated after DB initialization
        var handler = Services.GetRequiredService<Ratatoskr.Management.Agent.ManagementRequestHandler>();
        var client = Services.GetRequiredService<Ratatoskr.UI.Client.IRatatoskrBrokerManagementClient>();
        var hb = await handler.BuildHeartbeatAsync();
        client.Registry.RegisterHeartbeat(hb);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{UiBasePath}/api/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await HttpClient.SendAsync(
            request,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        // Read initial chunk containing snapshot event
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var buffer = new char[512];
        var readCount = await reader.ReadAsync(buffer.AsMemory(0, 512));
        var initialChunk = new string(buffer, 0, readCount);

        initialChunk.Should().Contain("event: snapshot");
        initialChunk.Should().Contain("sse-service");
    }
}
