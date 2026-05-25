using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// Base class for management API integration tests.
/// Sets up an HTTP client, authorization, and a seeded test database.
/// </summary>
public abstract class ManagementTestBase(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres), IDisposable
{
    private bool _disposed;
    protected HttpClient HttpClient { get; private set; } = null!;

    protected async Task StartManagementTestAsync(Action<IServiceCollection>? configure = null)
    {
        await StartTestAsync(services =>
        {
            // The test host pipeline calls app.UseAuthentication() before UseAuthorization(),
            // so we need *some* scheme registered or the middleware pipeline fails to build.
            // A permissive scheme that always authenticates as "test" lets the "RequireAssertion(true)"
            // policy below pass without real credentials.
            services
                .AddAuthentication(AllowAnonymousAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AllowAnonymousAuthenticationHandler>(
                    AllowAnonymousAuthenticationHandler.SchemeName,
                    _ => { }
                );

            services.AddAuthorization(o =>
                o.AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true))
            );

            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (_, opts) => opts.UseNpgsql(PostgresConnectionString)
            );

            configure?.Invoke(services);
        });

        await InitializeDatabase();
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        HttpClient?.Dispose();
        HttpClient = CreateHttpClient();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            HttpClient?.Dispose();
        }

        _disposed = true;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (!_disposed)
        {
            _disposed = true;
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            HttpClient?.Dispose();
        }

        await base.DisposeAsyncCore();
    }

    /// <summary>Seeds a poisoned outbox message and returns its ID.</summary>
    protected async Task<Guid> SeedPoisonedOutboxAsync(string? messageType = null)
    {
        var id = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var props = new MessageProperties
            {
                Type = messageType ?? "test.event",
                Id = Guid.NewGuid().ToString(),
            };
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { Data = "payload" }
            );
            var entity = OutboxMessageEntity.Create(content, props, time, "efcore");
            // Poison it by reaching max retries (ErrorCount >= maxRetries sets IsPoisoned)
            for (var i = 0; i < 3; i++)
            {
                entity.PublishFailed("simulated error", time, 3, TimeSpan.FromSeconds(1));
            }

            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });
        return id;
    }

    /// <summary>Seeds a poisoned inbox handler status and returns (messageId, handlerStatusId).</summary>
    protected async Task<(string MessageId, Guid HandlerStatusId)> SeedPoisonedInboxAsync(
        string? messageType = null
    )
    {
        string messageId = null!;
        var handlerStatusId = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var props = new MessageProperties { Type = messageType ?? "test.event" };
            var content = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { Data = "inbox-payload" }
            );
            var msg = InboxMessageEntity.Create(
                Guid.NewGuid().ToString(),
                "efcore",
                content,
                props,
                time
            );
            db.Set<InboxMessageEntity>().Add(msg);

            var handler = InboxHandlerStatusEntity.Create(msg.Id, "handler-a", time);
            for (var i = 0; i < 3; i++)
            {
                handler.MarkAsFailed("simulated inbox error", time, 3, TimeSpan.FromSeconds(1));
            }

            db.Set<InboxHandlerStatusEntity>().Add(handler);

            await db.SaveChangesAsync();
            messageId = msg.Id;
            handlerStatusId = handler.Id;
        });
        return (messageId, handlerStatusId);
    }

    /// <summary>
    /// Authenticates every request as a fixed "test" user. We need this because the test host's
    /// middleware pipeline unconditionally calls <c>UseAuthentication</c>, which blows up at build
    /// time if no scheme is registered. Real auth is covered in <c>ManagementAuthorizationTests</c>.
    /// </summary>
    private sealed class AllowAnonymousAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
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
