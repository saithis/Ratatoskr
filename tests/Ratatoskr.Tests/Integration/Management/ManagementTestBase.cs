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
using Ratatoskr.Management;
using Ratatoskr.Management.EfCore;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// A host running the per-service management REST API over a seeded database.
/// </summary>
public abstract class ManagementTestBase(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres), IDisposable
{
    /// <summary>The service name the test host announces.</summary>
    protected const string ServiceName = "test-service";

    /// <summary>The replica identity the test host announces.</summary>
    protected const string InstanceId = "test-instance";

    /// <summary>The management API root for the seeded DbContext.</summary>
    protected const string ContextUrl = "/ratatoskr/api/v1/contexts/TestDbContext";

    /// <summary>The outbox operations for the seeded DbContext.</summary>
    protected const string OutboxUrl = $"{ContextUrl}/outbox";

    /// <summary>The inbox operations for the seeded DbContext.</summary>
    protected const string InboxUrl = $"{ContextUrl}/inbox";

    private bool _disposed;

    protected HttpClient HttpClient { get; private set; } = null!;

    protected async Task StartManagementTestAsync(Action<IServiceCollection>? configure = null)
    {
        await StartTestAsync(services =>
        {
            services
                .AddAuthentication(BearerLikeAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, BearerLikeAuthenticationHandler>(
                    BearerLikeAuthenticationHandler.SchemeName,
                    _ => { }
                );

            services
                .AddAuthorizationBuilder()
                .AddPolicy("RatatoskrAdmin", p => p.RequireAssertion(_ => true));

            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox());
            });

            services.AddDbContext<TestDbContext>(
                (_, opts) => opts.UseNpgsql(PostgresConnectionString)
            );

            services.AddRatatoskrManagementAgent(agent =>
            {
                agent.ServiceName = ServiceName;
                agent.InstanceId = InstanceId;
                agent.UseEfCore();
            });

            configure?.Invoke(services);
        });

        await InitializeDatabase();
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
            HttpClient?.Dispose();
        }

        _disposed = true;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (!_disposed)
        {
            _disposed = true;
            HttpClient?.Dispose();
        }

        await base.DisposeAsyncCore();
    }

    /// <summary>Seeds a poisoned outbox message and returns its id.</summary>
    protected Task<Guid> SeedPoisonedOutboxAsync(string? messageType = null) =>
        SeedOutboxAsync(messageType, poisoned: true);

    /// <summary>Seeds an outbox message, optionally poisoned, and returns its id.</summary>
    protected async Task<Guid> SeedOutboxAsync(string? messageType = null, bool poisoned = true)
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

            if (poisoned)
            {
                // Three failures against a max of three is what actually sets IsPoisoned; setting
                // the flag directly would skip the counters the management API reports on.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    entity.PublishFailed("simulated error", time, 3, TimeSpan.FromSeconds(1));
                }
            }

            db.Set<OutboxMessageEntity>().Add(entity);
            await db.SaveChangesAsync();
            id = entity.Id;
        });
        return id;
    }

    /// <summary>Seeds a poisoned inbox handler status and returns its message and handler ids.</summary>
    protected async Task<(string MessageId, Guid HandlerStatusId)> SeedPoisonedInboxAsync(
        string? messageType = null,
        string handlerKey = "handler-a"
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
            var message = InboxMessageEntity.Create(
                Guid.NewGuid().ToString(),
                "efcore",
                content,
                props,
                time
            );
            db.Set<InboxMessageEntity>().Add(message);

            var handler = InboxHandlerStatusEntity.Create(message.Id, handlerKey, time);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                handler.MarkAsFailed("simulated inbox error", time, 3, TimeSpan.FromSeconds(1));
            }

            db.Set<InboxHandlerStatusEntity>().Add(handler);

            await db.SaveChangesAsync();
            messageId = message.Id;
            handlerStatusId = handler.Id;
        });
        return (messageId, handlerStatusId);
    }

    /// <summary>Adds a second poisoned handler to an existing inbox message.</summary>
    protected async Task<Guid> SeedAdditionalPoisonedHandlerAsync(string messageId, string handlerKey)
    {
        var handlerStatusId = Guid.Empty;
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            var time = ctx.ServiceProvider.GetRequiredService<TimeProvider>();
            var handler = InboxHandlerStatusEntity.Create(messageId, handlerKey, time);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                handler.MarkAsFailed("simulated inbox error", time, 3, TimeSpan.FromSeconds(1));
            }

            db.Set<InboxHandlerStatusEntity>().Add(handler);
            await db.SaveChangesAsync();
            handlerStatusId = handler.Id;
        });
        return handlerStatusId;
    }

    /// <summary>
    /// Authenticates every request as a fixed operator, reporting a scheme that is not a cookie
    /// scheme. That matters: antiforgery applies only to ambient credentials, so this stands in
    /// for the bearer-token callers the per-service API exists to serve.
    /// </summary>
    private sealed class BearerLikeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        internal const string SchemeName = "TestBearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "test-operator"),
                    new Claim(ClaimTypes.NameIdentifier, "operator-1"),
                ],
                SchemeName
            );
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
