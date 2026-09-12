using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// The per-service REST API and the dashboard facade are generated from one route tree, and this
/// is what keeps that true. Every operation in the protocol is exercised over both surfaces and
/// has to answer identically.
/// </summary>
/// <remarks>
/// Walking every route also catches a class of minimal-API mistake that no single feature test
/// would: a handler whose only parameter is <c>HttpContext</c> binds as a <c>RequestDelegate</c>,
/// so its result is discarded and the caller gets an empty 200 that looks like success.
/// </remarks>
public class ManagementSurfaceParityTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : DashboardTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task EveryOperation_AnswersOnBothSurfaces()
    {
        await StartDashboardAsync();
        await WaitForDiscoveryAsync();

        foreach (var probe in Probes())
        {
            var direct = await InvokeAsync(probe, "/ratatoskr/api/v1");
            var viaDashboard = await InvokeAsync(probe, DashboardServiceUrl);

            direct
                .Status.Should()
                .Be(
                    HttpStatusCode.OK,
                    $"'{probe.Operation}' must answer on the per-service API ({probe.Method} {probe.Path})"
                );
            viaDashboard
                .Status.Should()
                .Be(
                    HttpStatusCode.OK,
                    $"'{probe.Operation}' must answer on the dashboard facade ({probe.Method} {probe.Path})"
                );

            direct
                .Body.Should()
                .NotBeNullOrWhiteSpace(
                    $"'{probe.Operation}' returned an empty body on the per-service API, which usually means "
                        + "its handler bound as a RequestDelegate and its result was discarded"
                );
            viaDashboard
                .Body.Should()
                .NotBeNullOrWhiteSpace($"'{probe.Operation}' returned an empty body on the dashboard facade");

            IsJson(direct.Body).Should().BeTrue($"'{probe.Operation}' must answer with JSON");
            IsJson(viaDashboard.Body).Should().BeTrue($"'{probe.Operation}' must answer with JSON");
        }
    }

    [Test]
    public async Task EveryOperationName_IsCoveredByAProbe()
    {
        // Keeps the parity test honest: adding an operation without a probe fails here rather
        // than quietly shrinking the coverage of the test above.
        await Task.CompletedTask;

        Probes()
            .Select(probe => probe.Operation)
            .Should()
            .BeEquivalentTo(ManagementOperationNames.All);
    }

    private IEnumerable<Probe> Probes()
    {
        const string context = "/contexts/TestDbContext";
        var missing = Guid.NewGuid();

        yield return new Probe(ManagementOperationNames.ServiceDescribe, HttpMethod.Get, "/describe");
        yield return new Probe(ManagementOperationNames.ContextsList, HttpMethod.Get, "/contexts");
        yield return new Probe(ManagementOperationNames.ContextHealth, HttpMethod.Get, $"{context}/health");

        foreach (var (area, list, count, get, requeue, delete, requeueMatching, deleteMatching) in
            new[]
            {
                (
                    "outbox",
                    ManagementOperationNames.OutboxList,
                    ManagementOperationNames.OutboxCount,
                    ManagementOperationNames.OutboxGet,
                    ManagementOperationNames.OutboxRequeue,
                    ManagementOperationNames.OutboxDelete,
                    ManagementOperationNames.OutboxRequeueMatching,
                    ManagementOperationNames.OutboxDeleteMatching
                ),
                (
                    "inbox",
                    ManagementOperationNames.InboxList,
                    ManagementOperationNames.InboxCount,
                    ManagementOperationNames.InboxGet,
                    ManagementOperationNames.InboxRequeue,
                    ManagementOperationNames.InboxDelete,
                    ManagementOperationNames.InboxRequeueMatching,
                    ManagementOperationNames.InboxDeleteMatching
                ),
            })
        {
            yield return new Probe(list, HttpMethod.Get, $"{context}/{area}");
            yield return new Probe(count, HttpMethod.Get, $"{context}/{area}/count");

            // A well-formed request for a row that does not exist: the operation has to answer,
            // and a ProblemDetails 404 is an answer, so the probe expects OK only where a
            // successful shape is possible. Detail lookups therefore use a seeded row.
            yield return new Probe(get, HttpMethod.Get, $"{context}/{area}/{{seeded-{area}}}");

            yield return new Probe(
                requeue,
                HttpMethod.Post,
                $"{context}/{area}/requeue",
                () => new MutateByIdsRequest { Ids = [missing] }
            );
            yield return new Probe(
                delete,
                HttpMethod.Post,
                $"{context}/{area}/delete",
                () => new MutateByIdsRequest { Ids = [missing] }
            );
            yield return new Probe(
                requeueMatching,
                HttpMethod.Post,
                $"{context}/{area}/requeue-matching",
                () => new MutateMatchingRequest
                {
                    Filter = new MessageFilter { Status = MessageStatusFilter.Poisoned },
                }
            );
            yield return new Probe(
                deleteMatching,
                HttpMethod.Post,
                $"{context}/{area}/delete-matching",
                () => new MutateMatchingRequest
                {
                    Filter = new MessageFilter { Status = MessageStatusFilter.Poisoned },
                }
            );
        }

        yield return new Probe(
            ManagementOperationNames.InboxRequeueMessage,
            HttpMethod.Post,
            $"{context}/inbox/messages/{{seeded-message}}/requeue"
        );
    }

    private async Task<(HttpStatusCode Status, string Body)> InvokeAsync(Probe probe, string root)
    {
        // Each surface runs against its own freshly seeded rows, so the detail and
        // requeue-by-message probes always have something real to address.
        var outboxId = await SeedPoisonedOutboxAsync();
        var (messageId, handlerStatusId) = await SeedPoisonedInboxAsync();

        var path = probe
            .Path.Replace("{seeded-outbox}", outboxId.ToString(), StringComparison.Ordinal)
            .Replace("{seeded-inbox}", handlerStatusId.ToString(), StringComparison.Ordinal)
            .Replace("{seeded-message}", messageId, StringComparison.Ordinal);

        using var request = new HttpRequestMessage(probe.Method, root + path);
        if (probe.Payload is not null)
        {
            request.Content = JsonContent.Create(probe.Payload(), probe.Payload().GetType());
        }

        using var response = await HttpClient.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static bool IsJson(string body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Probe(
        string Operation,
        HttpMethod Method,
        string Path,
        Func<object>? Payload = null
    );
}
