using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using InventoryService;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Examples;

/// <summary>
/// The inventory example is what the playground dashboard aggregates in multi-service mode. These
/// tests pin the two things the example exists to demonstrate: a service that exposes only the
/// management API (no UI package), and a service with two DbContexts where one has a single half
/// configured.
/// </summary>
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public sealed class InventoryServiceHttpTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private const string ContextsUrl = "/ratatoskr/api/v1/efcore/contexts";

    private readonly string _testId = Guid.NewGuid().ToString("N");
    private WebApplicationFactory<InventoryServiceAppMarker>? _factory;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership is transferred to _factory and disposed in DisposeAsync."
    )]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "IDisposableAnalyzers.Correctness",
        "IDISP003:Dispose previous before re-assigning",
        Justification = "Called once per test instance; TUnit creates a fresh instance per test."
    )]
    private async Task<HttpClient> StartAsync()
    {
        var inventoryDb = $"inv_{_testId}";
        var auditDb = $"aud_{_testId}";
        await CreateDatabaseAsync(inventoryDb);
        await CreateDatabaseAsync(auditDb);

        var factory = new WebApplicationFactory<InventoryServiceAppMarker>().WithWebHostBuilder(
            builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:inventorydb",
                    ConnectionStringFor(inventoryDb)
                );
                builder.UseSetting("ConnectionStrings:auditdb", ConnectionStringFor(auditDb));
                builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");
            }
        );

        _factory = factory;
        return factory.CreateClient();
    }

    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = database,
            MaxPoolSize = 4,
        }.ToString();

    private async Task CreateDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task ManagementApi_ListsBothDbContextsWithTheirConfiguredHalves()
    {
        using var client = await StartAsync();

        using var response = await client.GetAsync(ContextsUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ContextListResponse>();
        var byName = payload!.Contexts.ToDictionary(c => c.Name, StringComparer.Ordinal);

        byName.Keys.Should().BeEquivalentTo("InventoryDbContext", "AuditDbContext");
        byName["InventoryDbContext"].HasOutbox.Should().BeTrue();
        byName["InventoryDbContext"].HasInbox.Should().BeTrue();

        // AuditDbContext is registered with UseOutbox() only. The dashboard relies on this to
        // disable the Inbox toggle instead of querying an endpoint that answers 404.
        byName["AuditDbContext"].HasOutbox.Should().BeTrue();
        byName["AuditDbContext"].HasInbox.Should().BeFalse();

        using var inboxResponse = await client.GetAsync(
            $"{ContextsUrl}/AuditDbContext/inbox/poisoned"
        );
        inboxResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task FailingReservation_BecomesAPoisonedInboxRowVisibleThroughTheManagementApi()
    {
        using var client = await StartAsync();

        // The example handler throws for SKUs starting with FAIL, so the inbox exhausts its
        // retries and poisons the handler status. That is what the dashboard workbench lists.
        using var post = await client.PostAsJsonAsync(
            "/inventory/reservations",
            new { sku = "FAIL-widget", quantity = 1 }
        );
        post.StatusCode.Should().Be(HttpStatusCode.Accepted);

        JsonElement? poisoned = null;
        await WaitForAsync(
            async () =>
            {
                var payload = await client.GetFromJsonAsync<JsonElement>(
                    $"{ContextsUrl}/InventoryDbContext/inbox/poisoned"
                );
                var items = payload.GetProperty("items");
                if (items.GetArrayLength() == 0)
                {
                    return false;
                }

                poisoned = items[0];
                return true;
            },
            TimeSpan.FromSeconds(60),
            "no poisoned inbox row appeared for the failing reservation"
        );

        poisoned!
            .Value.GetProperty("handlerKey")
            .GetString()
            .Should()
            .Be("inventory.reserve-stock");
        poisoned.Value.GetProperty("lastError").GetString().Should().Contain("FAIL-widget");
    }

    [Test]
    public async Task SuccessfulReservation_IsHandledAndStaysOutOfThePoisonList()
    {
        using var client = await StartAsync();

        using var post = await client.PostAsJsonAsync(
            "/inventory/reservations",
            new { sku = "WIDGET-1", quantity = 3 }
        );
        post.StatusCode.Should().Be(HttpStatusCode.Accepted);

        ReservationDto? reservation = null;
        await WaitForAsync(
            async () =>
            {
                var rows = await client.GetFromJsonAsync<List<ReservationDto>>(
                    "/inventory/reservations"
                );
                reservation = rows?.Find(r =>
                    string.Equals(r.Sku, "WIDGET-1", StringComparison.Ordinal)
                );
                return reservation is not null;
            },
            TimeSpan.FromSeconds(60),
            "the reservation was never handled"
        );

        reservation!.Quantity.Should().Be(3);

        var poisoned = await client.GetFromJsonAsync<JsonElement>(
            $"{ContextsUrl}/InventoryDbContext/inbox/poisoned"
        );
        poisoned.GetProperty("totalCount").GetInt64().Should().Be(0);
    }

    private static async Task WaitForAsync(
        Func<Task<bool>> probe,
        TimeSpan timeout,
        string failureMessage
    )
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out after {timeout}: {failureMessage}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    private sealed record ContextListEntry(string Name, bool HasOutbox, bool HasInbox);

    private sealed record ContextListResponse(List<ContextListEntry> Contexts);

    private sealed record ReservationDto(Guid Id, string Sku, int Quantity);
}
