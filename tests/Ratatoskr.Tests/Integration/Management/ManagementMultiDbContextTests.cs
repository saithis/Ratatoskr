using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// One service can register several DbContexts, and the dashboard has to keep them apart: it
/// lists them, addresses poison rows per context, and only offers the halves a context actually
/// configured. <see cref="SecondTestDbContext"/> is registered with <c>UseOutbox()</c> only,
/// which is the case that used to be misreported because every management DbContext implements
/// both interfaces regardless of what was configured.
/// </summary>
public class ManagementMultiDbContextTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    private const string BaseUrl = "/ratatoskr/api/v1/efcore/contexts";

    private string SecondPostgresConnectionString =>
        new Npgsql.NpgsqlConnectionStringBuilder(PostgresFixture.ConnectionString)
        {
            Database = $"test_{TestId}_second",
            MaxPoolSize = 2,
        }.ToString();

    public override async Task StartTestAsync(Action<IServiceCollection>? configure = null)
    {
        await CreateSecondDatabaseAsync();
        await base.StartTestAsync(configure);
    }

    private Task StartWithSecondContextAsync() =>
        StartManagementTestAsync(
            services =>
                services.AddDbContext<SecondTestDbContext>(
                    (_, opts) => opts.UseNpgsql(SecondPostgresConnectionString)
                ),
            // Outbox only. The management API must advertise and enforce that.
            bus => bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseOutbox())
        );

    private async Task CreateSecondDatabaseAsync()
    {
        await using (var connection = new Npgsql.NpgsqlConnection(PostgresFixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"test_{TestId}_second\"";
            await command.ExecuteNonQueryAsync();
        }

        // The metrics poller for the second context queries as soon as the host starts, so the
        // schema has to exist before the host is built.
        var options = new DbContextOptionsBuilder<SecondTestDbContext>()
            .UseNpgsql(SecondPostgresConnectionString)
            .Options;
        await using var db = new SecondTestDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    [Test]
    public async Task ContextsEndpoint_ListsEveryRegisteredDbContextWithItsConfiguredHalves()
    {
        await StartWithSecondContextAsync();

        using var response = await HttpClient.GetAsync(BaseUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ContextListResponse>();
        var byName = payload!.Contexts.ToDictionary(c => c.Name, StringComparer.Ordinal);

        byName.Keys.Should().BeEquivalentTo(nameof(TestDbContext), nameof(SecondTestDbContext));

        byName[nameof(TestDbContext)].HasOutbox.Should().BeTrue();
        byName[nameof(TestDbContext)].HasInbox.Should().BeTrue();

        byName[nameof(SecondTestDbContext)].HasOutbox.Should().BeTrue();
        byName[nameof(SecondTestDbContext)]
            .HasInbox.Should()
            .BeFalse("SecondTestDbContext was registered with UseOutbox() only");
    }

    [Test]
    public async Task InboxEndpoints_OnAnOutboxOnlyContext_Return404()
    {
        await StartWithSecondContextAsync();

        using var response = await HttpClient.GetAsync(
            $"{BaseUrl}/{nameof(SecondTestDbContext)}/inbox/poisoned"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("No inbox is registered");
    }

    [Test]
    public async Task OutboxEndpoints_OnAnOutboxOnlyContext_AreAvailable()
    {
        await StartWithSecondContextAsync();

        using var response = await HttpClient.GetAsync(
            $"{BaseUrl}/{nameof(SecondTestDbContext)}/outbox/poisoned"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Test]
    public async Task PoisonedLists_AreScopedToTheAddressedDbContext()
    {
        await StartWithSecondContextAsync();
        await SeedPoisonedOutboxAsync();

        using var firstResponse = await HttpClient.GetAsync(
            $"{BaseUrl}/{nameof(TestDbContext)}/outbox/poisoned"
        );
        using var secondResponse = await HttpClient.GetAsync(
            $"{BaseUrl}/{nameof(SecondTestDbContext)}/outbox/poisoned"
        );

        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Each context is a separate database; a poisoned row in one must not surface in the other.
        first.GetProperty("totalCount").GetInt64().Should().Be(1);
        first
            .GetProperty("items")[0]
            .GetProperty("dbContext")
            .GetString()
            .Should()
            .Be(nameof(TestDbContext));
        second.GetProperty("totalCount").GetInt64().Should().Be(0);
    }

    [Test]
    public async Task HealthEndpoint_AnswersPerDbContext()
    {
        await StartWithSecondContextAsync();

        using var response = await HttpClient.GetAsync(
            $"{BaseUrl}/{nameof(SecondTestDbContext)}/health"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var health = await response.Content.ReadFromJsonAsync<JsonElement>();
        health.GetProperty("dbContextName").GetString().Should().Be(nameof(SecondTestDbContext));
        // The dashboard sums these across contexts for its badge, so they have to be present
        // even before the gauge poller has run.
        health.TryGetProperty("poisonedOutboxCount", out _).Should().BeTrue();
        health.TryGetProperty("poisonedInboxCount", out _).Should().BeTrue();
    }

    [Test]
    public async Task UnknownContextName_Returns404()
    {
        await StartWithSecondContextAsync();

        using var response = await HttpClient.GetAsync(
            $"{BaseUrl}/NoSuchDbContext/outbox/poisoned"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NoSuchDbContext");
    }

    private sealed record ContextListEntry(string Name, bool HasOutbox, bool HasInbox);

    private sealed record ContextListResponse(List<ContextListEntry> Contexts);
}
