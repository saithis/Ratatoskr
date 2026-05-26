using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Management;
using Ratatoskr.EfCore.Management.Endpoints.Inbox;
using Ratatoskr.EfCore.Management.Endpoints.Outbox;
using Ratatoskr.Management;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Covers the behaviours that came out of the management API review — keyset pagination,
/// page-size clamping, search-filter substring matching, bulk request validation, problem-details
/// shape, the duplicate-short-name guard, and the "no configurators → no endpoints" path.
///
/// These assertions are deliberately granular so that any regression in a single area fails
/// exactly one test rather than bleeding across the broader list/bulk tests.
/// </summary>
public class ManagementCoverageTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private const string OutboxBaseUrl = "/ratatoskr/api/v1/efcore/contexts/TestDbContext/outbox";
    private const string InboxBaseUrl = "/ratatoskr/api/v1/efcore/contexts/TestDbContext/inbox";

    [Test]
    public async Task Pagination_WalksEveryRowExactlyOnce_AcrossCursors()
    {
        await StartManagementTestAsync();

        const int total = 5;
        var seeded = new HashSet<Guid>();
        for (var i = 0; i < total; i++)
        {
            seeded.Add(await SeedPoisonedOutboxAsync());
        }

        var collected = new List<Guid>();
        string? cursor = null;
        // pageSize=2 forces three round-trips for five seeded rows, exercising the "carry-over"
        // behaviour where the last row of one page must not reappear as the first of the next.
        long? firstTotalCount = null;
        do
        {
            var url =
                $"{OutboxBaseUrl}/poisoned?pageSize=2"
                + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var response = await HttpClient.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            var items = body.GetProperty("items").ToElementList();
            collected.AddRange(items.Select(i => i.GetProperty("id").GetGuid()));

            firstTotalCount ??= body.GetProperty("totalCount").GetInt64();
            body.GetProperty("totalCount")
                .GetInt64()
                .Should()
                .Be(
                    firstTotalCount.Value,
                    "totalCount must reflect the full filtered set, not the remainder after the cursor"
                );

            cursor =
                body.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
        } while (cursor is not null);

        collected.Should().OnlyHaveUniqueItems("the keyset cursor must never replay a row");
        collected
            .Should()
            .BeEquivalentTo(seeded, "every seeded row must appear exactly once across pages");
    }

    [Test]
    public async Task Pagination_PageSizeOverMax_IsClampedSilently()
    {
        await StartManagementTestAsync();
        // Seed one more than the cap so the clamp is observable in the response.
        for (var i = 0; i < PaginationOptions.MaxPageSize + 1; i++)
        {
            await SeedPoisonedOutboxAsync();
        }

        using var response = await HttpClient.GetAsync($"{OutboxBaseUrl}/poisoned?pageSize=10000");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items")
            .GetArrayLength()
            .Should()
            .Be(
                PaginationOptions.MaxPageSize,
                "the server must clamp an oversize pageSize to MaxPageSize"
            );
    }

    [Test]
    public async Task Pagination_InvalidCursor_ReturnsProblemDetails()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync(
            $"{OutboxBaseUrl}/poisoned?cursor=not-a-cursor"
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Bad request");
        body.GetProperty("type").GetString().Should().Contain("bad-request");
        body.GetProperty("detail").GetString().Should().Contain("cursor");
    }

    [Test]
    public async Task SearchFilter_MatchesSubstring()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync("order.placed");
        await SeedPoisonedOutboxAsync("payment.failed");

        // "order" is a substring of "order.placed" but not "payment.failed"
        using var response = await HttpClient.GetAsync($"{OutboxBaseUrl}/poisoned?search=order");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").ToElementList();
        items.Should().HaveCount(1);
        items[0].GetProperty("messageType").GetString().Should().Be("order.placed");
        body.GetProperty("totalCount").GetInt64().Should().Be(1);
    }

    [Test]
    public async Task SearchFilter_WildcardsInUserInput_AreEscaped()
    {
        await StartManagementTestAsync();
        await SeedPoisonedOutboxAsync("order.placed");
        await SeedPoisonedOutboxAsync("order.shipped");

        // "%" is a SQL LIKE wildcard; if we forgot to escape it server-side, this query would
        // match both rows above. Escaped correctly, it matches zero rows because no message
        // has the literal string "order.%" in its serialized properties.
        using var response = await HttpClient.GetAsync(
            $"{OutboxBaseUrl}/poisoned?search={Uri.EscapeDataString("order.%")}"
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items")
            .GetArrayLength()
            .Should()
            .Be(0, "LIKE wildcards in user input must be escaped so they match literally");
    }

    [Test]
    public async Task BulkRequeue_EmptyIdList_ReturnsProblemDetails()
    {
        await StartManagementTestAsync();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{OutboxBaseUrl}/poisoned/requeue")
        {
            Content = JsonContent.Create(
                new BulkRequeueOutboxEndpoint.BulkRequeueOutboxRequest([])
            ),
        };
        using var response = await HttpClient.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("ids");
    }

    [Test]
    public async Task BulkRequeue_ContainsGuidEmpty_ReturnsProblemDetails()
    {
        await StartManagementTestAsync();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{OutboxBaseUrl}/poisoned/requeue")
        {
            Content = JsonContent.Create(
                new BulkRequeueOutboxEndpoint.BulkRequeueOutboxRequest([Guid.Empty])
            ),
        };
        using var response = await HttpClient.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("empty Guid");
    }

    [Test]
    public async Task BulkRequeue_TooManyIds_ReturnsProblemDetails()
    {
        await StartManagementTestAsync();

        var ids = Enumerable
            .Range(0, BulkRequestValidator.MaxIds + 1)
            .Select(_ => Guid.NewGuid())
            .ToList();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{OutboxBaseUrl}/poisoned/requeue")
        {
            Content = JsonContent.Create(
                new BulkRequeueOutboxEndpoint.BulkRequeueOutboxRequest(ids)
            ),
        };
        using var response = await HttpClient.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().Contain("limit");
    }

    [Test]
    public async Task BulkDeleteInbox_ContainsGuidEmpty_ReturnsProblemDetails()
    {
        await StartManagementTestAsync();

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{InboxBaseUrl}/poisoned")
        {
            Content = JsonContent.Create(
                new BulkDeleteInboxEndpoint.BulkDeleteInboxRequest([Guid.Empty])
            ),
        };
        using var response = await HttpClient.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Test]
    public async Task UnknownContext_Returns404WithProblemDetails()
    {
        await StartManagementTestAsync();

        using var response = await HttpClient.GetAsync(
            "/ratatoskr/api/v1/efcore/contexts/DoesNotExist/outbox/poisoned"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("Not found");
        body.GetProperty("type").GetString().Should().Contain("not-found");
        body.GetProperty("detail").GetString().Should().Contain("DoesNotExist");
    }
}

/// <summary>
/// Pure DI/unit coverage for management API composition. Kept outside <see cref="ManagementTestBase"/>
/// because these tests deliberately poke the container and the endpoint extensions directly
/// without needing a real Postgres or Rabbit container.
/// </summary>
public class ManagementCompositionTests
{
    [Test]
    public void EfCoreManagementProviderLookup_DuplicateShortNames_ThrowsAtStartup()
    {
        // Two providers that report the same short "TestDbContext" but different full names
        // would silently collapse into one dictionary entry without the TryAdd guard — which
        // in turn would route every request to whichever provider was registered last.
        IEnumerable<IEfCoreManagementDbContextDescriptor> providers =
        [
            new StubDescriptor("TestDbContext", "App.Alpha.TestDbContext"),
            new StubDescriptor("TestDbContext", "App.Beta.TestDbContext"),
        ];

        var act = () => new EfCoreManagementDbContextLookup(providers, serviceProvider: null);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*TestDbContext*App.Alpha*App.Beta*");
    }

    [Test]
    public void MapRatatoskrManagementApi_NoConfigurators_IsNoOp()
    {
        // A host that references Ratatoskr but doesn't register any durability transports should
        // still be able to call MapRatatoskrManagementApi without exploding — the call returns
        // the same endpoint builder and maps nothing.
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddAuthorizationBuilder()
            .AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true));

        using var sp = services.BuildServiceProvider();
        var routes = new MinimalEndpointRouteBuilder(sp);

        var result = routes.MapRatatoskrManagementApi("RatatoskrAdmin");

        result.Should().BeSameAs(routes, "the extension should return the builder for chaining");
        routes
            .DataSources.Should()
            .BeEmpty("no configurators were registered, so no endpoints should exist");
    }

    private sealed class StubDescriptor(string shortName, string fullName)
        : IEfCoreManagementDbContextDescriptor
    {
        public Type DbContextType { get; } = null!;
        public string DbContextName => shortName;
        public string DbContextFullName => fullName;
        public bool HasOutbox => false;
        public bool HasInbox => false;
        public DateTimeOffset? LastOutboxProcessingAt => null;
        public DateTimeOffset? LastInboxProcessingAt => null;

        public DbContext GetDbContext(IServiceProvider serviceProvider) =>
            throw new NotSupportedException();
    }

    private sealed class MinimalEndpointRouteBuilder(IServiceProvider sp)
        : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder
    {
        private readonly List<Microsoft.AspNetCore.Routing.EndpointDataSource> _ds = [];

        public IServiceProvider ServiceProvider { get; } = sp;
        public ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource> DataSources => _ds;

        public Microsoft.AspNetCore.Builder.IApplicationBuilder CreateApplicationBuilder() =>
            new Microsoft.AspNetCore.Builder.ApplicationBuilder(ServiceProvider);
    }
}
