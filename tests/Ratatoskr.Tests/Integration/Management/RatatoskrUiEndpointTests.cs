using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI.Store;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// The dashboard hosted in the same process as the service it manages, reached through the
/// in-process transport.
/// </summary>
public class RatatoskrUiEndpointTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : DashboardTestBase(rabbitMq, postgres)
{
    // The dashboard answers in the control plane's own JSON dialect — enums as names — so
    // tests read it back the same way rather than with web defaults that would reject them.
    private static readonly JsonSerializerOptions WebJson = ManagementJson.Options;

    [Test]
    public async Task StaticAssets_AreServedFromTheEmbeddedResources()
    {
        await StartDashboardAsync();

        using var index = await HttpClient.GetAsync("/ratatoskr/");
        index.StatusCode.Should().Be(HttpStatusCode.OK);
        index.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        // The CSP and nosniff headers are what keep a hostile payload rendered in the dashboard
        // from becoming script execution.
        index.Headers.GetValues("Content-Security-Policy").Should().NotBeEmpty();
        index.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");

        using var css = await HttpClient.GetAsync("/ratatoskr/css/dashboard.css");
        css.StatusCode.Should().Be(HttpStatusCode.OK);

        using var missing = await HttpClient.GetAsync("/ratatoskr/js/not-a-real-file.js");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Api_ListsTheTransportsAndTheServicesOnThem()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();

        using var transports = await HttpClient.GetAsync("/ratatoskr/api/transports");
        (await transports.Content.ReadFromJsonAsync<string[]>(WebJson))
            .Should()
            .BeEquivalentTo([Transport]);

        using var services = await HttpClient.GetAsync("/ratatoskr/api/services");
        var cards = await services.Content.ReadFromJsonAsync<ServiceCard[]>(WebJson);
        cards.Should().ContainSingle();
        cards![0].TransportName.Should().Be(Transport);
        cards[0].ServiceName.Should().Be(ServiceName);
        cards[0].ContextNames.Should().Contain("TestDbContext");
    }

    [Test]
    public async Task Api_DispatchesTheSharedOperationsThroughTheTransport()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();
        var id = await SeedPoisonedOutboxAsync();

        var url = $"{DashboardContextUrl}/outbox";

        using var list = await HttpClient.GetAsync(url);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await list.Content.ReadFromJsonAsync<CursorPage<OutboxListItem>>(WebJson);
        page!.Items.Should().ContainSingle(item => item.Id == id);

        using var requeue = await HttpClient.PostAsJsonAsync(
            $"{url}/requeue",
            new MutateByIdsRequest { Ids = [id] }
        );
        requeue.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Api_ForAServiceThatWasNeverSeen_FailsFast()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();

        using var response = await HttpClient.GetAsync(
            $"/ratatoskr/api/transports/{Transport}/services/never-heard-of-it/contexts/TestDbContext/outbox"
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be(ManagementErrorCodes.TargetUnreachable);
    }

    [Test]
    public async Task Api_ForAnUnknownTransport_ReturnsNotFound()
    {
        await StartDashboardAsync();

        using var response = await HttpClient.GetAsync(
            $"/ratatoskr/api/transports/no-such-transport/services/{ServiceName}/contexts/TestDbContext/outbox"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Mutations_LeaveAnAuditRecord()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();
        var id = await SeedPoisonedOutboxAsync();

        var url = $"{DashboardContextUrl}/outbox";
        using var requeue = await HttpClient.PostAsJsonAsync(
            $"{url}/requeue",
            new MutateByIdsRequest { Ids = [id] }
        );
        requeue.StatusCode.Should().Be(HttpStatusCode.OK);

        using var audit = await HttpClient.GetAsync("/ratatoskr/api/audit");
        audit.StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await audit.Content.ReadFromJsonAsync<DashboardAuditEntry[]>(WebJson);
        var entry = entries.Should().ContainSingle().Subject;
        entry.Operation.Should().Be(ManagementOperationNames.OutboxRequeue);
        entry.TransportName.Should().Be(Transport);
        entry.ServiceName.Should().Be(ServiceName);
        entry.Resource.Should().Be("TestDbContext");
        entry.Actor.Should().Be("operator-1");
        entry.Outcome.Should().Be(nameof(ManagementResultStatus.Ok));
        entry.RequestJson.Should().Contain(id.ToString());
        entry.CompletedAt.Should().BeOnOrAfter(entry.StartedAt);
    }

    [Test]
    public async Task Reads_AreNotAudited()
    {
        // A row per list refresh would bury the mutations nobody could then find.
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();

        var url = $"{DashboardContextUrl}/outbox";
        using var list = await HttpClient.GetAsync(url);
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        using var audit = await HttpClient.GetAsync("/ratatoskr/api/audit");
        (await audit.Content.ReadFromJsonAsync<DashboardAuditEntry[]>(WebJson)).Should().BeEmpty();
    }

    [Test]
    public async Task EventStream_SendsAnImmediateSnapshot()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ratatoskr/api/events");
        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var eventLine = await reader.ReadLineAsync(timeout.Token);
        var dataLine = await reader.ReadLineAsync(timeout.Token);

        eventLine.Should().Be("event: services");
        dataLine.Should().StartWith("data: [");
        dataLine.Should().Contain(ServiceName);
    }
}
