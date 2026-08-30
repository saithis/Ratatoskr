using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Multi-service mode: the browser only ever talks to the dashboard host, which relays every
/// management call to the selected remote service. These tests pin the relay contract — target
/// URL composition, method and body forwarding, and response pass-through — by standing in for
/// the remote service with a recording handler.
/// </summary>
public class RatatoskrUIMultiServiceTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private readonly ConcurrentQueue<RelayedRequest> _relayed = new();

    /// <summary>
    /// Boots the test host with the given remote services and a stand-in for their management
    /// API. The canned reply is passed in rather than stored on the test instance so tests stay
    /// free of shared mutable state.
    /// </summary>
    private Task StartWithRemoteServicesAsync(
        Action<RatatoskrUIOptions> configureUi,
        HttpStatusCode replyStatus = HttpStatusCode.OK,
        string replyBody = "{\"ok\":true}",
        string replyContentType = "application/json"
    ) =>
        StartManagementTestAsync(services =>
        {
            var options = new RatatoskrUIOptions();
            configureUi(options);
            // AddRatatoskrUI already registered a default instance in the test host; the last
            // singleton registration is the one MapRatatoskrUI resolves.
            services.AddSingleton(options);

            services
                .AddHttpClient(RatatoskrUIEndpointRouteBuilderExtensions.ProxyHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new RecordingRemoteHandler(_relayed, replyStatus, replyBody, replyContentType)
                );
        });

    [Test]
    public async Task Proxy_ComposesTargetUrlFromTheConfiguredManagementApiUrl()
    {
        await StartWithRemoteServicesAsync(ui =>
            ui.AddService("orders", "https://orders.internal/edge")
        );

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/ui-api/proxy/orders/system/metrics?window=5m"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _relayed.Should().ContainSingle();
        _relayed.TryPeek(out var relayed).Should().BeTrue();
        // The client appends only the endpoint path: the management API base path lives in the
        // configured URL, so it must appear exactly once.
        relayed!
            .Url.Should()
            .Be("https://orders.internal/edge/ratatoskr/api/v1/system/metrics?window=5m");
        relayed.Method.Should().Be("GET");
    }

    [Test]
    public async Task Proxy_WithCustomManagementApiPath_TargetsThatPath()
    {
        await StartWithRemoteServicesAsync(ui =>
            ui.AddService("legacy", "https://legacy.internal", "/admin/ratatoskr")
        );

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/ui-api/proxy/legacy/efcore/contexts"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _relayed.TryPeek(out var relayed).Should().BeTrue();
        relayed!.Url.Should().Be("https://legacy.internal/admin/ratatoskr/efcore/contexts");
    }

    [Test]
    public async Task Proxy_ResolvesServiceNamesThatNeedUrlEncoding()
    {
        await StartWithRemoteServicesAsync(ui =>
            ui.AddService("Order Service", "https://orders.internal")
        );

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/ui-api/proxy/Order%20Service/system/metrics"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _relayed.TryPeek(out var relayed).Should().BeTrue();
        relayed!.Url.Should().Be("https://orders.internal/ratatoskr/api/v1/system/metrics");
    }

    [Test]
    public async Task Proxy_ForwardsTheBodyOfADeleteRequest()
    {
        await StartWithRemoteServicesAsync(ui =>
            ui.AddService("orders", "https://orders.internal")
        );

        // Bulk delete carries its ids in a DELETE body. Dropping it turns "delete these ids"
        // into a malformed request, which is invisible from the dashboard.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/ratatoskr/ui-api/proxy/orders/efcore/contexts/TestDbContext/outbox/poisoned"
        )
        {
            Content = JsonContent.Create(new { ids }),
        };

        using var response = await HttpClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _relayed.TryPeek(out var relayed).Should().BeTrue();
        relayed!.Method.Should().Be("DELETE");
        relayed.Body.Should().NotBeNullOrEmpty();
        var relayedIds = JsonSerializer.Deserialize<BulkIdsPayload>(
            relayed.Body!,
            JsonSerializerOptions.Web
        );
        relayedIds!.Ids.Should().BeEquivalentTo(ids);
        relayed.ContentType.Should().StartWith("application/json");
    }

    [Test]
    public async Task Proxy_ForwardsPostBodies()
    {
        await StartWithRemoteServicesAsync(ui =>
            ui.AddService("orders", "https://orders.internal")
        );

        using var response = await HttpClient.PostAsJsonAsync(
            "/ratatoskr/ui-api/proxy/orders/efcore/contexts/TestDbContext/outbox/poisoned/requeue",
            new { ids = new[] { Guid.Empty } }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _relayed.TryPeek(out var relayed).Should().BeTrue();
        relayed!.Method.Should().Be("POST");
        relayed.Body.Should().Contain("ids");
    }

    [Test]
    public async Task Proxy_RelaysTheRemoteStatusCodeAndBodyBack()
    {
        await StartWithRemoteServicesAsync(
            ui => ui.AddService("orders", "https://orders.internal"),
            HttpStatusCode.NotFound,
            "{\"title\":\"no such context\"}",
            "application/problem+json"
        );

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/ui-api/proxy/orders/efcore/contexts/Missing/health"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no such context");
    }

    [Test]
    public async Task Proxy_WithUnknownServiceName_Returns404AndDoesNotCallOut()
    {
        await StartWithRemoteServicesAsync(ui =>
            ui.AddService("orders", "https://orders.internal")
        );

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/ui-api/proxy/shipping/system/metrics"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("'orders'");
        _relayed.Should().BeEmpty();
    }

    [Test]
    public async Task ConfigEndpoint_ListsRemoteServicesWithTheirResolvedManagementApiUrl()
    {
        await StartWithRemoteServicesAsync(ui =>
        {
            ui.LocalServiceName = "Playground Host";
            ui.AddService("orders", "https://orders.internal");
            ui.AddService("shipping", "https://shipping.internal");
        });

        using var response = await HttpClient.GetAsync("/ratatoskr/ui-api/config");

        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        config.GetProperty("localServiceName").GetString().Should().Be("Playground Host");
        config.GetProperty("includeLocalService").GetBoolean().Should().BeTrue();

        var remotes = config.GetProperty("remoteServices");
        remotes.GetArrayLength().Should().Be(2);
        remotes[0].GetProperty("name").GetString().Should().Be("orders");
        remotes[0]
            .GetProperty("managementApiUrl")
            .GetString()
            .Should()
            .Be("https://orders.internal/ratatoskr/api/v1");
        remotes[1].GetProperty("name").GetString().Should().Be("shipping");
    }

    [Test]
    public async Task ConfigEndpoint_PointsTheLocalServiceAtTheConfiguredManagementApiPath()
    {
        // MapRatatoskrManagementApi takes a basePath; the dashboard has to follow it rather than
        // assuming the default, or the local service silently 404s.
        await StartWithRemoteServicesAsync(ui =>
        {
            ui.LocalManagementApiPath = "/admin/ratatoskr";
            ui.AddService("orders", "https://orders.internal");
        });

        using var response = await HttpClient.GetAsync("/ratatoskr/ui-api/config");

        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        config.GetProperty("defaultBasePath").GetString().Should().Be("/admin/ratatoskr");
    }

    [Test]
    public async Task ConfigEndpoint_CanExcludeTheLocalService()
    {
        await StartWithRemoteServicesAsync(ui =>
        {
            ui.IncludeLocalService = false;
            ui.AddService("orders", "https://orders.internal");
        });

        using var response = await HttpClient.GetAsync("/ratatoskr/ui-api/config");

        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        config.GetProperty("includeLocalService").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task Proxy_IsNotMappedWhenNoRemoteServiceIsRegistered()
    {
        // A single-service host should not expose an outbound request surface at all.
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/ui-api/proxy/orders/system/metrics"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record RelayedRequest(
        string Method,
        string Url,
        string? Body,
        string? ContentType
    );

    private sealed record BulkIdsPayload(List<Guid> Ids);

    /// <summary>
    /// Stands in for the remote service's management API: records what the relay sent and
    /// answers with a canned response.
    /// </summary>
    private sealed class RecordingRemoteHandler(
        ConcurrentQueue<RelayedRequest> relayed,
        HttpStatusCode replyStatus,
        string replyBody,
        string replyContentType
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            relayed.Enqueue(
                new RelayedRequest(
                    request.Method.Method,
                    request.RequestUri!.ToString(),
                    body,
                    request.Content?.Headers.ContentType?.ToString()
                )
            );

            return new HttpResponseMessage(replyStatus)
            {
                Content = new StringContent(replyBody, Encoding.UTF8, replyContentType),
            };
        }
    }
}
