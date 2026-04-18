using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Ensures the RabbitMQ management route binds DI parameters correctly at startup (minimal APIs
/// otherwise infer a parameter named <c>consumer</c> as a request body).
/// </summary>
public class RabbitMqManagementHealthEndpointTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    [Test]
    public async Task RabbitMqHealth_Get_ReturnsOk_WhenRabbitMqTransportRegistered()
    {
        await StartTestAsync(services =>
        {
            services.AddAuthentication(AllowAnonymousAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AllowAnonymousAuthenticationHandler>(
                    AllowAnonymousAuthenticationHandler.SchemeName, _ => { });

            services.AddAuthorization(o =>
                o.AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true)));

            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel($"mgmt-health-{TestId}", c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
            });
        });

        var client = CreateHttpClient();
        var response = await client.GetAsync("/ratatoskr/api/v1/rabbitmq/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class AllowAnonymousAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        internal const string SchemeName = "AllowAnonymousRabbitMqMgmtHealth";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
