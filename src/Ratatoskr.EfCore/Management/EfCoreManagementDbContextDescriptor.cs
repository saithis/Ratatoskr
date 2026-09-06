using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.EfCore.Internal;

namespace Ratatoskr.EfCore.Management;

internal sealed class EfCoreManagementDbContextDescriptor<TDbContext>(
    IServiceProvider serviceProvider
) : IEfCoreManagementDbContextDescriptor
    where TDbContext : DbContext, IOutboxDbContext, IInboxDbContext
{
    private readonly OutboxProcessor<TDbContext>? _outboxProcessor = serviceProvider.GetService<
        OutboxProcessor<TDbContext>
    >();
    private readonly InboxProcessor<TDbContext>? _inboxProcessor = serviceProvider.GetService<
        InboxProcessor<TDbContext>
    >();

    public Type DbContextType { get; } = typeof(TDbContext);
    public string DbContextName => DbContextType.Name;
    public string DbContextFullName => DbContextType.FullName ?? DbContextType.Name;
    public bool HasOutbox { get; } =
        serviceProvider.GetService<OutboxOptionsHolder<TDbContext>>() is not null;
    public bool HasInbox { get; } =
        serviceProvider.GetService<InboxOptionsHolder<TDbContext>>() is not null;
    public DateTimeOffset? LastOutboxProcessingAt => _outboxProcessor?.LastSuccessfulProcessingAt;
    public DateTimeOffset? LastInboxProcessingAt => _inboxProcessor?.LastSuccessfulProcessingAt;

    public DbContext GetDbContext(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<TDbContext>();
}
