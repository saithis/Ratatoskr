using AwesomeAssertions;
using Medallion.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Management;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.EfCore;
using Ratatoskr.Management.EfCore.Idempotency;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// The operation log is written on every mutation, so it grows without bound unless something
/// prunes it.
/// </summary>
public class ManagementOperationCleanupTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    protected override TimeProvider CreateTimeProvider() =>
        new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero));

    [Test]
    public async Task Cleanup_RunsForAnOutboxOnlyService()
    {
        // The failure this guards against: attaching the sweep to the inbox cleanup service, which
        // only runs when an inbox is configured *and* a retention period is set. An outbox-only
        // service is an ordinary shape, and it has an operation log like everything else.
        await StartOutboxOnlyServiceAsync();

        // Retention defaults to seven days. Ages are set on the records rather than by moving the
        // clock, because moving it would also fire the worker's own timer and make the test race
        // its subject.
        var expired = await SeedOperationAsync(
            ManagementOperationState.Completed,
            completedAt: FakeTime.GetUtcNow().AddDays(-8)
        );
        var recent = await SeedOperationAsync(
            ManagementOperationState.Completed,
            completedAt: FakeTime.GetUtcNow()
        );
        var interrupted = await SeedOperationAsync(
            ManagementOperationState.InProgress,
            completedAt: null
        );

        var removed = await CleanupAsync();

        removed.Should().Be(1);
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<OutboxOnlyDbContext>();
            (await db.Set<ManagementOperationEntity>().FindAsync(expired)).Should().BeNull();
            (await db.Set<ManagementOperationEntity>().FindAsync(recent)).Should().NotBeNull();

            // An in-progress record means a run was interrupted; deleting it would turn a resumable
            // operation into a silently restarted one.
            (await db.Set<ManagementOperationEntity>().FindAsync(interrupted)).Should().NotBeNull();
        });
    }

    [Test]
    public async Task Cleanup_SkipsWhenAnotherReplicaHoldsTheLock()
    {
        // Every replica runs the worker against a shared table; without the lock they would all
        // delete the same rows at once and deadlock each other for no benefit.
        await StartOutboxOnlyServiceAsync();

        var locks = Services.GetRequiredService<IDistributedLockProvider>();
        await using var held = await locks.AcquireLockAsync(
            ManagementOperationCleanupService<OutboxOnlyDbContext>.LockName
        );

        var service = Services
            .GetServices<IHostedService>()
            .OfType<ManagementOperationCleanupService<OutboxOnlyDbContext>>()
            .Single();

        (await service.TryCleanupWithLockAsync(CancellationToken.None)).Should().BeFalse();
    }

    private async Task<int> CleanupAsync()
    {
        var service = Services
            .GetServices<IHostedService>()
            .OfType<ManagementOperationCleanupService<OutboxOnlyDbContext>>()
            .Single();
        return await service.CleanupAsync(CancellationToken.None);
    }

    private async Task StartOutboxOnlyServiceAsync()
    {
        // The base fixture already creates the Ratatoskr tables for this test's database, and the
        // outbox-only context maps the same ones.
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
                bus.AddEfCoreDurability<OutboxOnlyDbContext>(d => d.UseOutbox())
            );
            services.AddDbContext<OutboxOnlyDbContext>(
                (_, opts) => opts.UseNpgsql(PostgresConnectionString)
            );
            services.AddRatatoskrManagementAgent(agent =>
            {
                agent.ServiceName = "outbox-only";
                agent.InstanceId = "instance-1";
                agent.UseEfCore();
            });
            services.AddRatatoskrManagementOperationCleanup<OutboxOnlyDbContext>();
        });
    }

    private async Task<Guid> SeedOperationAsync(
        ManagementOperationState state,
        DateTimeOffset? completedAt
    )
    {
        var id = Guid.NewGuid();
        await InScopeAsync(async ctx =>
        {
            var db = ctx.ServiceProvider.GetRequiredService<OutboxOnlyDbContext>();
            db.Set<ManagementOperationEntity>()
                .Add(
                    new ManagementOperationEntity
                    {
                        OperationId = id,
                        CreatedAt = FakeTime.GetUtcNow(),
                        CompletedAt = completedAt,
                        Operation = ManagementOperationNames.OutboxRequeue,
                        State = state,
                    }
                );
            await db.SaveChangesAsync();
        });
        return id;
    }
}

/// <summary>A DbContext with an outbox and no inbox — the shape the cleanup worker must not miss.</summary>
public class OutboxOnlyDbContext(DbContextOptions<OutboxOnlyDbContext> options)
    : DbContext(options),
        IOutboxDbContext,
        IInboxDbContext
{
    public OutboxStagingCollection OutboxMessages { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddRatatoskrEfCoreModel(Database);
    }
}
