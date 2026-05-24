using System.Net;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.EfCore;
using Ratatoskr.Management;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class ManagementAuthorizationTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task ManagementApi_UnauthenticatedRequest_Returns401()
    {
        // Policy that requires an authenticated user (not just "allow all")
        await StartTestAsync(services =>
        {
            // Use a scheme that returns 401 on challenge (no real auth in tests)
            services
                .AddAuthentication("Reject")
                .AddScheme<AuthenticationSchemeOptions, AlwaysRejectHandler>("Reject", _ => { });
            services.AddAuthorization(o =>
                o.AddPolicy("RatatoskrAdmin", p => p.RequireAuthenticatedUser())
            );

            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (_, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
        });

        await InitializeDatabase();
        var client = CreateHttpClient();

        // No authentication → 401
        var response = await client.GetAsync(
            "/ratatoskr/api/v1/efcore/contexts/TestDbContext/outbox/poisoned"
        );
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task MapRatatoskrManagementApi_UnknownPolicy_ThrowsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o =>
            o.AddPolicy("ExistingPolicy", p => p.RequireAssertion(_ => true))
        );
        services.AddRatatoskr(bus => bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox()));
        services.AddDbContext<TestDbContext>(opts => opts.UseInMemoryDatabase("throwtest"));

        await using var sp = services.BuildServiceProvider();
        var endpointBuilder = new MinimalEndpointRouteBuilder(sp);

        var act = () => endpointBuilder.MapRatatoskrManagementApi("NonExistentPolicy");
        act.Should().Throw<InvalidOperationException>().WithMessage("*NonExistentPolicy*");
    }
}

/// <summary>Authentication handler that never authenticates any user, returning 401 on challenge.</summary>
file sealed class AlwaysRejectHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}

/// <summary>Minimal <see cref="IEndpointRouteBuilder"/> for unit-testing MapRatatoskrManagementApi.</summary>
file sealed class MinimalEndpointRouteBuilder(IServiceProvider serviceProvider)
    : IEndpointRouteBuilder
{
    private readonly List<EndpointDataSource> _dataSources = [];

    public IServiceProvider ServiceProvider { get; } = serviceProvider;
    public ICollection<EndpointDataSource> DataSources => _dataSources;

    public IApplicationBuilder CreateApplicationBuilder() =>
        new ApplicationBuilder(ServiceProvider);
}
