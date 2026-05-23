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
    public bool HasOutbox => DbContextType.IsAssignableTo(typeof(IOutboxDbContext));
    public bool HasInbox => DbContextType.IsAssignableTo(typeof(IInboxDbContext));
    public DateTimeOffset? LastOutboxProcessingAt => _outboxProcessor?.LastSuccessfulProcessingAt;
    public DateTimeOffset? LastInboxProcessingAt => _inboxProcessor?.LastSuccessfulProcessingAt;

    public DbContext GetDbContext(IServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<TDbContext>();
}
