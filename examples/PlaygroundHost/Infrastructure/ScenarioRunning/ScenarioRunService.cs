using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlaygroundHost.Persistence;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public sealed class ScenarioRunService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScenarioRunService> logger,
    IEnumerable<IScenario> scenarios,
    TimeProvider time)
{
    private readonly Dictionary<string, IScenario> _bySlug = scenarios.ToDictionary(s => s.Slug, StringComparer.OrdinalIgnoreCase);

    private async Task<T> WithPlaygroundDb<T>(Func<PlaygroundDbContext, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>());
    }

    private async Task WithPlaygroundDb(Func<PlaygroundDbContext, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>());
    }

    public IReadOnlyList<ScenarioCatalogEntry> ListCatalog() =>
        _bySlug.Values
            .Select(s => new ScenarioCatalogEntry(
                s.Slug,
                s.Title,
                s.Description,
                s.Topic,
                s.RequiresDangerConfirmation,
                s.DangerConfirmationText))
            .OrderBy(s => s.Slug)
            .ToList();

    public async Task<ScenarioStartResult> StartRunAsync(string slug, bool confirmDanger, CancellationToken cancellationToken)
    {
        if (!_bySlug.TryGetValue(slug, out var scenario))
            return new ScenarioStartResult(null, null, $"Unknown scenario '{slug}'.");

        if (scenario.RequiresDangerConfirmation && !confirmDanger)
            return new ScenarioStartResult(
                null,
                null,
                "This scenario requires confirmDanger=true (acknowledge the risk in the dashboard).");

        var runId = Guid.NewGuid();
        await WithPlaygroundDb(async db =>
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            db.Runs.Add(new PlaygroundRunEntity
            {
                Id = runId,
                ScenarioSlug = slug,
                State = "Running",
                StartedAt = time.GetUtcNow(),
                StepIndex = 0,
                CurrentStep = "execute",
            });
            await db.SaveChangesAsync(cancellationToken);
        });

        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

        _ = RunInBackgroundAsync(runId, scenario, executionCts, timeoutCts);

        return new ScenarioStartResult(runId, scenario.Title, null);
    }

    private async Task RunInBackgroundAsync(
        Guid runId,
        IScenario scenario,
        CancellationTokenSource executionCts,
        CancellationTokenSource timeoutCts)
    {
        using var pollShutdown = new CancellationTokenSource();
        using var pollLoopCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, pollShutdown.Token);
        var pollTask = PollCancelRequestedAsync(runId, executionCts, pollLoopCts.Token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger($"Scenario:{scenario.Slug}");
            var ctx = new ScenarioExecutionContext(runId, scope.ServiceProvider, scopeFactory, log);
            var verdict = await scenario.ExecuteAsync(ctx, executionCts.Token);
            // Persist terminal state without the execution token: cooperative cancel sets executionCts
            // cancelled while scenarios like cancel-smoke still return a normal Passed verdict.
            await WithPlaygroundDb(async db =>
            {
                var row = await db.Runs.FirstAsync(r => r.Id == runId, CancellationToken.None);
                row.State = verdict.Passed ? "Passed" : "Failed";
                row.CompletedAt = time.GetUtcNow();
                row.Detail = verdict.Reason;
                await db.SaveChangesAsync(CancellationToken.None);
            });
        }
        catch (OperationCanceledException)
        {
            var (terminal, detail) = await TryResolveCancelOrTimeoutAsync(runId, cancellationToken: default);
            await MarkTerminalAsync(runId, terminal, detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scenario {Slug} failed", scenario.Slug);
            await MarkTerminalAsync(runId, "Failed", ex.Message);
        }
        finally
        {
            await pollShutdown.CancelAsync();
            try
            {
                await pollTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            catch
            {
                // Poll loop may observe linked-token cancellation or a disposed scope factory during host teardown.
            }

            executionCts.Dispose();
            timeoutCts.Dispose();
        }
    }

    private async Task PollCancelRequestedAsync(
        Guid runId,
        CancellationTokenSource executionCts,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested && !executionCts.IsCancellationRequested)
            {
                await Task.Delay(200, stoppingToken);
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
                    var cancel = await db.Runs.AsNoTracking()
                        .Where(r => r.Id == runId)
                        .Select(r => r.CancelRequested)
                        .FirstOrDefaultAsync(stoppingToken);
                    if (cancel)
                    {
                        await executionCts.CancelAsync();
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private Task<(string State, string Detail)> TryResolveCancelOrTimeoutAsync(Guid runId, CancellationToken cancellationToken) =>
        WithPlaygroundDb(async db =>
        {
            var row = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
            return row?.CancelRequested == true ? ("Cancelled", "Cancelled.") : ("Failed", "Timed out.");
        });

    private Task MarkTerminalAsync(Guid runId, string state, string? detail) =>
        WithPlaygroundDb(async db =>
        {
            var row = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId);
            if (row is null) return;
            row.State = state;
            row.CompletedAt = time.GetUtcNow();
            row.Detail = detail;
            await db.SaveChangesAsync();
        });

    public Task<ScenarioRunStatusDto?> GetStatusAsync(Guid runId, CancellationToken cancellationToken) =>
        WithPlaygroundDb(async db =>
        {
            var row = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (row is null) return null;
            return new ScenarioRunStatusDto(row.Id, row.ScenarioSlug, row.State, row.StartedAt, row.CompletedAt, row.Detail);
        });

    public Task<bool> RequestCancelAsync(Guid runId, CancellationToken cancellationToken) =>
        WithPlaygroundDb(async db =>
        {
            var row = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (row is null || row.State is "Passed" or "Failed" or "Cancelled")
                return false;
            row.CancelRequested = true;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        });
}
