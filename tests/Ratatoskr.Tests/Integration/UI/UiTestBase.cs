using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;
using Ratatoskr.UI;

namespace Ratatoskr.Tests.Integration.UI;

/// <summary>
/// Base class for Ratatoskr Management UI proxy integration tests.
/// Wires up the full stack: management API + UI proxy + a local backend pointing at TestDbContext.
/// </summary>
public abstract class UiTestBase(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    protected HttpClient HttpClient { get; private set; } = null!;

    protected async Task StartUiTestAsync(Action<IServiceCollection>? configure = null)
    {
        await StartTestAsync(services =>
        {
            // Allow-all auth scheme for tests — real auth coverage is in ManagementAuthorizationTests.
            services.AddAuthentication(AllowAnonymousAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AllowAnonymousAuthenticationHandler>(
                    AllowAnonymousAuthenticationHandler.SchemeName, _ => { });

            services.AddAuthorization(o =>
                o.AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true)));

            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox());
            });

            services.AddDbContext<TestDbContext>((_, opts) =>
                opts.UseNpgsql(PostgresConnectionString));

            // Pre-configure the UI proxy with a single local backend ("TestService")
            services.AddRatatoskrUi(options =>
            {
                options.PolicyName = "RatatoskrAdmin";
                options.AddLocalBackend("TestService");
            });

            configure?.Invoke(services);
        });

        await InitializeDatabase();
        HttpClient = CreateHttpClient();
    }

    /// <summary>Seeds a poisoned outbox message and returns its ID.</summary>
    protected async Task<Guid> SeedPoisonedOutboxAsync(string? messageType = null)
    {
        var id = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var props = new MessageProperties { Type = messageType ?? "test.event", Id = Guid.NewGuid().ToString() };
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Data = "payload" });
            var entity = OutboxMessageEntity.Create(content, props, time, "efcore");
            for (var i = 0; i < 3; i++)
                entity.PublishFailed("simulated error", time, 3, TimeSpan.FromSeconds(1));
            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });
        return id;
    }

    private sealed class AllowAnonymousAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        internal const string SchemeName = "AllowAnonymousTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
