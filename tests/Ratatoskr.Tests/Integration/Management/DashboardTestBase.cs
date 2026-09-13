using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ratatoskr.Management;
using Ratatoskr.Management.Registry;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// A host that is both the managed service and the dashboard watching it, connected by the
/// in-process transport. The co-hosted shape is worth testing on its own: it is the smallest
/// deployment Ratatoskr supports, and it is where an agent and a dashboard are most likely to
/// disagree about who owns what.
/// </summary>
public abstract class DashboardTestBase(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    /// <summary>The name the in-process transport is registered under.</summary>
    protected const string Transport = "in-process";

    /// <summary>The dashboard facade's root for the co-hosted service.</summary>
    protected string DashboardServiceUrl =>
        $"/ratatoskr/api/transports/{Transport}/services/{ServiceName}";

    /// <summary>The dashboard facade's root for the co-hosted service's seeded DbContext.</summary>
    protected string DashboardContextUrl => $"{DashboardServiceUrl}/contexts/TestDbContext";

    private string DashboardDatabase => $"dash_{TestId}";

    private string DashboardConnectionString =>
        new NpgsqlConnectionStringBuilder(PostgresFixture.ConnectionString)
        {
            Database = DashboardDatabase,
            MaxPoolSize = 3,
        }.ToString();

    /// <summary>Starts the agent, the dashboard and its store in one host.</summary>
    protected async Task StartDashboardAsync(
        Action<IServiceCollection>? configure = null,
        Action<RatatoskrBuilder>? configureBus = null
    )
    {
        await CreateDashboardDatabaseAsync();

        await StartManagementTestAsync(services =>
        {
            services.AddRatatoskrManagementAgent(agent => agent.AddInProcess(Transport));
            services.AddRatatoskrDashboard(dashboard =>
            {
                dashboard.UseStore(
                    db => db.UseNpgsql(DashboardConnectionString),
                    store => store.AutoMigrate = true
                );
                dashboard.Configure(options => options.StaleAfter = TimeSpan.FromSeconds(45));
            });

            configure?.Invoke(services);
        }, configureBus);
    }

    /// <summary>Waits until the co-hosted service has announced itself to the dashboard.</summary>
    protected async Task WaitForDiscoveryAsync()
    {
        var registry = Services.GetRequiredService<ServiceRegistry>();
        await WaitForConditionAsync(
            () => registry.GetService(Transport, ServiceName) is not null,
            TimeSpan.FromSeconds(20),
            "the in-process service never announced itself"
        );
    }

    private async Task CreateDashboardDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{DashboardDatabase}\"";
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            // Already exists.
        }
    }
}
