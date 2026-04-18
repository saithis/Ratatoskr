using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.EfCore;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;

namespace Ratatoskr.Tests.Integration.UI;

public class UiProxyTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : UiTestBase(rabbitMq, postgres)
{
    // ── /backends ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Backends_ReturnsRegisteredBackends()
    {
        await StartUiTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/backends");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var backends = body.EnumerateArray().ToList();
        backends.Should().HaveCount(1);
        backends[0].GetProperty("name").GetString().Should().Be("TestService");
        backends[0].GetProperty("isLocal").GetBoolean().Should().BeTrue();
    }

    // ── /dashboard ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Dashboard_ReturnsBackendHealth()
    {
        await StartUiTestAsync();

        var response = await HttpClient.GetAsync("/ratatoskr/api/v1/dashboard");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var backends = body.GetProperty("backends").EnumerateArray().ToList();
        backends.Should().ContainSingle(b => b.GetProperty("name").GetString() == "TestService");
    }

    // ── Local backend passthrough ───────────────────────────────────────────────────

    [Test]
    public async Task LocalBackend_Passthrough_ReachesManagementEndpoint()
    {
        await StartUiTestAsync();

        var response = await HttpClient.GetAsync(
            "/ratatoskr/api/v1/backends/TestService/efcore/contexts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var contexts = body.GetProperty("contexts").EnumerateArray().ToList();
        contexts.Should().ContainSingle(c => c.GetProperty("name").GetString() == "TestDbContext");
    }

    [Test]
    public async Task LocalBackend_Passthrough_PoisonedOutbox_ReturnsPoisonedMessages()
    {
        await StartUiTestAsync();
        await SeedPoisonedOutboxAsync();

        var response = await HttpClient.GetAsync(
            "/ratatoskr/api/v1/backends/TestService/efcore/contexts/TestDbContext/outbox/poisoned");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalCount").GetInt64().Should().BeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task LocalBackend_Passthrough_RequeueOutbox_ReturnsOk()
    {
        await StartUiTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        var response = await HttpClient.PostAsync(
            $"/ratatoskr/api/v1/backends/TestService/efcore/contexts/TestDbContext/outbox/poisoned/{id}/requeue",
            null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task LocalBackend_Passthrough_DeleteOutbox_ReturnsOk()
    {
        await StartUiTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        var response = await HttpClient.DeleteAsync(
            $"/ratatoskr/api/v1/backends/TestService/efcore/contexts/TestDbContext/outbox/poisoned/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Case-insensitive backend name lookup ────────────────────────────────────────

    [Test]
    public async Task LocalBackend_CaseInsensitiveName_Resolves()
    {
        await StartUiTestAsync();

        var response = await HttpClient.GetAsync(
            "/ratatoskr/api/v1/backends/TESTSERVICE/efcore/contexts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Unknown backend ─────────────────────────────────────────────────────────────

    [Test]
    public async Task UnknownBackend_Returns404()
    {
        await StartUiTestAsync();

        var response = await HttpClient.GetAsync(
            "/ratatoskr/api/v1/backends/DoesNotExist/efcore/contexts");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Authorization ────────────────────────────────────────────────────────────────

    [Test]
    public async Task UiRoutes_RequireAuthorization_UnauthenticatedReturns401()
    {
        await StartTestAsync(services =>
        {
            services.AddAuthentication("Reject")
                .AddScheme<AuthenticationSchemeOptions, AlwaysRejectHandler>("Reject", _ => { });
            services.AddAuthorization(o =>
                o.AddPolicy("RatatoskrAdmin", p => p.RequireAuthenticatedUser()));

            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });
            services.AddDbContext<TestDbContext>((_, opts) =>
                opts.UseNpgsql(PostgresConnectionString));

            services.AddRatatoskrUi(options =>
            {
                options.PolicyName = "RatatoskrAdmin";
                options.AddLocalBackend("TestService");
            });
        });

        await InitializeDatabase();
        var client = CreateHttpClient();

        var response = await client.GetAsync("/ratatoskr/api/v1/backends");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── No policy (open access) ──────────────────────────────────────────────────────

    [Test]
    public async Task UiRoutes_NoPolicyName_AreAccessibleAnonymously()
    {
        await StartTestAsync(services =>
        {
            services.AddAuthentication();
            services.AddAuthorization(o =>
                o.AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true)));

            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });
            services.AddDbContext<TestDbContext>((_, opts) =>
                opts.UseNpgsql(PostgresConnectionString));

            // PolicyName intentionally left empty → no RequireAuthorization on proxy routes
            services.AddRatatoskrUi(options =>
            {
                options.AddLocalBackend("TestService");
            });
        });

        await InitializeDatabase();
        var client = CreateHttpClient();

        var response = await client.GetAsync("/ratatoskr/api/v1/backends");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>Authentication handler that never authenticates any user, returning 401 on challenge.</summary>
file sealed class AlwaysRejectHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
