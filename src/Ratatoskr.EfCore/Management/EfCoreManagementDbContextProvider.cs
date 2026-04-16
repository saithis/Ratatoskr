using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal sealed class EfCoreManagementDbContextProvider<TDbContext> : IEfCoreManagementDbContextProvider
    where TDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    private readonly OutboxProcessor<TDbContext>? _outboxProcessor;
    private readonly InboxProcessor<TDbContext>? _inboxProcessor;

    public EfCoreManagementDbContextProvider(
        EfCoreMetricsState metricsState,
        IServiceProvider serviceProvider)
    {
        MetricsState = metricsState;
        _outboxProcessor = serviceProvider.GetService<OutboxProcessor<TDbContext>>();
        _inboxProcessor = serviceProvider.GetService<InboxProcessor<TDbContext>>();
        HasOutbox = serviceProvider.GetService<OutboxOptionsHolder<TDbContext>>() is not null;
        HasInbox = serviceProvider.GetService<InboxOptionsHolder<TDbContext>>() is not null;
    }

    public string DbContextName { get; } = typeof(TDbContext).Name;
    public bool HasOutbox { get; }
    public bool HasInbox { get; }
    public EfCoreMetricsState MetricsState { get; }
    public string MetricsContextKey { get; } = typeof(TDbContext).FullName ?? typeof(TDbContext).Name;
    public DateTimeOffset? LastOutboxProcessingAt => _outboxProcessor?.LastSuccessfulProcessingAt;
    public DateTimeOffset? LastInboxProcessingAt => _inboxProcessor?.LastSuccessfulProcessingAt;

    public DbContext GetDbContext(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<TDbContext>();
}
