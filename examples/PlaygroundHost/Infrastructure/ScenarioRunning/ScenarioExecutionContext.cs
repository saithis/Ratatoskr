using PlaygroundHost.Persistence;
using Ratatoskr;

namespace PlaygroundHost.Infrastructure.ScenarioRunning;

public sealed class ScenarioExecutionContext(
    Guid runId,
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger logger)
{
    public Guid RunId { get; } = runId;

    public string ScenarioRunId => RunId.ToString("D");

    /// <summary>
    ///     Root <see cref="IServiceProvider" /> for this scenario run (one async scope for the whole
    ///     <see cref="IScenario.ExecuteAsync" />).
    ///     Use <see cref="ScopeFactory" /> when you need a separate scope (for example a fresh <c>DbContext</c> per poll).
    /// </summary>
    public IServiceProvider Services { get; } = services;

    public IServiceScopeFactory ScopeFactory { get; } = scopeFactory;

    public ILogger Logger { get; } = logger;

    public List<string> StepsCompleted { get; } = [];

    private TimeProvider? _timeProvider;
    public TimeProvider TimeProvider => _timeProvider ??= Services.GetRequiredService<TimeProvider>();

    private PublisherDbContext? _publisherDb;
    public PublisherDbContext PublisherDb => _publisherDb ??= Services.GetRequiredService<PublisherDbContext>();

    private IRatatoskr? _ratatoskr;
    public IRatatoskr Ratatoskr => _ratatoskr ??= Services.GetRequiredService<IRatatoskr>();

    public string RabbitMqConnectionString
    {
        get
        {
            if (string.IsNullOrEmpty(field))
            {
                var cfg = GetRequired<IConfiguration>();
                field = cfg.GetConnectionString("rabbitmq")
                        ?? throw new InvalidOperationException("rabbitmq connection string missing.");
            }
            return field;
        }
    }

    public T GetRequired<T>()
        where T : notnull
    {
        return Services.GetRequiredService<T>();
    }
}
