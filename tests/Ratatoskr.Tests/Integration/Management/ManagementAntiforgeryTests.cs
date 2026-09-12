using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Http;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Antiforgery guards against a browser attaching a credential the user never meant to send. It
/// therefore applies to ambient credentials and to nothing else: applying it everywhere would
/// break exactly the programmatic callers the per-service REST API exists to serve.
/// </summary>
public class ManagementAntiforgeryTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : DashboardTestBase(rabbitMq, postgres)
{
    [Test]
    public async Task ExplicitCredentialMutation_NeedsNoToken()
    {
        // The default test host authenticates with an explicit-credential scheme, standing in for
        // a bearer token or an API key. Nothing attaches those on the browser's behalf.
        await StartManagementTestAsync();
        var id = await SeedPoisonedOutboxAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [id] }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task AmbientCredentialMutation_WithoutAToken_IsRejectedAndChangesNothing()
    {
        await StartAmbientAuthenticatedHostAsync();
        var id = await SeedPoisonedOutboxAsync();

        using var response = await HttpClient.PostAsJsonAsync(
            $"{OutboxUrl}/requeue",
            new MutateByIdsRequest { Ids = [id] }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var entity = await db.Set<OutboxMessageEntity>().FindAsync(id);
            entity!.IsPoisoned.Should().BeTrue("a rejected request must not have mutated anything");
        });
    }

    [Test]
    public async Task AmbientCredentialRead_IsNotAffected()
    {
        // Reads carry no CSRF exposure, and demanding a token to look at a list would make the
        // dashboard unusable for the people it exists for.
        await StartAmbientAuthenticatedHostAsync();

        using var response = await HttpClient.GetAsync(OutboxUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task AmbientCredentialMutation_WithTheIssuedToken_Succeeds()
    {
        await StartAmbientAuthenticatedHostAsync(withDashboard: true);
        await WaitForDiscoveryAsync();
        var id = await SeedPoisonedOutboxAsync();

        // The dashboard hands the token out over its own endpoint, which is how a long-lived tab
        // refreshes one without reloading the page.
        using var issued = await HttpClient.GetAsync("/ratatoskr/api/antiforgery");
        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await issued.Content.ReadFromJsonAsync<AntiforgeryToken>();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{OutboxUrl}/requeue")
        {
            Content = JsonContent.Create(new MutateByIdsRequest { Ids = [id] }),
        };
        request.Headers.Add(token!.HeaderName, token.RequestToken);
        CopyCookies(issued, request);

        using var response = await HttpClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task ACookieSchemeIsClassifiedAsAmbient_WhateverItIsNamed()
    {
        // The classification keys on the handler type, not the scheme name. A cookie scheme
        // registered as "Portal" is every bit as forgeable as one registered as "Cookies".
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddAntiforgery();
        services.AddDataProtection();
        services.AddAuthentication("Portal").AddCookie("Portal");
        services.AddSingleton<ManagementAntiforgeryOptions>();
        services.AddSingleton<ManagementAntiforgeryFilter>();

        await using var provider = services.BuildServiceProvider();
        var filter = provider.GetRequiredService<ManagementAntiforgeryFilter>();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "Portal")
            ),
        };
        httpContext.Request.Method = HttpMethods.Post;

        var reached = false;
        var result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(httpContext),
            _ =>
            {
                reached = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            }
        );

        reached.Should().BeFalse("the request carried no antiforgery token");
        result.Should().NotBeNull();
    }

    private async Task StartAmbientAuthenticatedHostAsync(bool withDashboard = false)
    {
        void Configure(IServiceCollection services)
        {
            // The test's own scheme is declared ambient, which is the documented escape hatch for
            // a credential the browser attaches that is not a cookie handler — a session cookie
            // set by a reverse proxy, say.
            var options = new ManagementAntiforgeryOptions();
            options.AdditionalAmbientSchemes.Add("TestBearer");
            services.AddSingleton(options);
        }

        if (withDashboard)
        {
            await StartDashboardAsync(Configure);
        }
        else
        {
            await StartManagementTestAsync(Configure);
        }
    }

    private static void CopyCookies(HttpResponseMessage from, HttpRequestMessage to)
    {
        if (from.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            to.Headers.Add(
                "Cookie",
                string.Join("; ", cookies.Select(cookie => cookie.Split(';')[0]))
            );
        }
    }

    private sealed record AntiforgeryToken(string HeaderName, string RequestToken);

    private sealed class DefaultEndpointFilterInvocationContext(HttpContext httpContext)
        : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;

        public override IList<object?> Arguments { get; } = [];

        public override T GetArgument<T>(int index) => throw new NotSupportedException();
    }
}
