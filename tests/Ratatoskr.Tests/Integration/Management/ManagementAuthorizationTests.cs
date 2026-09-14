using System.Net;
using System.Net.Http.Json;
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
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore;
using Ratatoskr.Management.Http;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class ManagementAuthorizationTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : ManagementTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task ManagementApi_UnauthenticatedRequest_IsRefused()
    {
        await StartTestAsync(services =>
        {
            services
                .AddAuthentication("Reject")
                .AddScheme<AuthenticationSchemeOptions, AlwaysRejectHandler>("Reject", _ => { });
            services
                .AddAuthorizationBuilder()
                .AddPolicy("RatatoskrAdmin", p => p.RequireAuthenticatedUser());

            services.AddRatatoskr(bus => bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox()));
            services.AddDbContext<TestDbContext>(
                (_, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddRatatoskrManagementAgent(agent =>
            {
                agent.ServiceName = ServiceName;
                agent.InstanceId = InstanceId;
                agent.UseEfCore();
            });
        });

        await InitializeDatabase();
        using var client = CreateHttpClient();

        using var response = await client.GetAsync(OutboxUrl);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ManagementApi_SeparatePolicies_AreIndependentlyEnforced()
    {
        // Reading a backlog count and reading a customer's order payload are different risks, so
        // they are different policies; this proves the split is real and not decorative.
        await StartTestAsync(services =>
        {
            services
                .AddAuthentication(AllowAllHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AllowAllHandler>(
                    AllowAllHandler.SchemeName,
                    _ => { }
                );

            services
                .AddAuthorizationBuilder()
                .AddPolicy("Metadata", p => p.RequireAssertion(_ => true))
                .AddPolicy("Payloads", p => p.RequireAssertion(_ => false))
                .AddPolicy("Mutations", p => p.RequireAssertion(_ => true));

            services.AddRatatoskr(bus =>
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox())
            );
            services.AddDbContext<TestDbContext>(
                (_, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddRatatoskrManagementAgent(agent =>
            {
                agent.ServiceName = ServiceName;
                agent.InstanceId = InstanceId;
                agent.UseEfCore();
            });

            services.AddSingleton(
                new ManagementApiPolicies("Metadata", "Payloads", "Mutations", "Mutations", "Mutations")
            );
        });

        await InitializeDatabase();
        using var client = CreateHttpClient();
        var id = await SeedPoisonedOutboxAsync();

        using var list = await client.GetAsync(OutboxUrl);
        list.StatusCode.Should().Be(HttpStatusCode.OK, "the metadata policy allows listing");

        using var detail = await client.GetAsync($"{OutboxUrl}/{id}");
        detail.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the payload policy denies reading bodies");

        using var requeue = await client.PostAsJsonAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [id] }
        );
        requeue.StatusCode.Should().Be(HttpStatusCode.OK, "the mutation policy allows requeueing");
    }

    [Test]
    public async Task MapRatatoskrManagementApi_UnknownPolicy_FailsAtStartup()
    {
        // A management endpoint that silently never authorizes is worse than one that refuses to
        // start, so the policy is checked while the routes are being built.
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddAuthorizationBuilder()
            .AddPolicy("ExistingPolicy", p => p.RequireAssertion(_ => true));
        services.AddRatatoskr(bus => bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox()));
        services.AddDbContext<TestDbContext>(opts => opts.UseInMemoryDatabase("policy-check"));
        services.AddRatatoskrManagementEfCore();

        await using var provider = services.BuildServiceProvider();
        var endpoints = new MinimalEndpointRouteBuilder(provider);

        var act = () => endpoints.MapRatatoskrManagementApi("NonExistentPolicy");

        act.Should().Throw<InvalidOperationException>().WithMessage("*NonExistentPolicy*");
    }
}

/// <summary>Never authenticates, so the pipeline challenges and the caller sees 401.</summary>
file sealed class AlwaysRejectHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}

/// <summary>Authenticates every request, so authorization policies are what decide the outcome.</summary>
file sealed class AllowAllHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "AllowAll";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "operator")],
            SchemeName
        );
        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(
                    new System.Security.Claims.ClaimsPrincipal(identity),
                    SchemeName
                )
            )
        );
    }
}

/// <summary>A route builder with no host, for asserting what mapping does at startup.</summary>
file sealed class MinimalEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
{
    private readonly List<EndpointDataSource> _dataSources = [];

    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    public ICollection<EndpointDataSource> DataSources => _dataSources;

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}
