using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.Local;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Durable replacement for <see cref="LocalMessageSender"/> when the inbox pattern is configured
/// alongside the local transport.
/// <para>
/// Instead of writing only to the in-memory channel, this sender:
/// <list type="number">
/// <item><description>Persists inbox-managed handler statuses to the database (crash-safe).</description></item>
/// <item><description>Writes to the in-memory channel so <c>LocalTransportConsumer</c> can dispatch non-inbox handlers.</description></item>
/// </list>
/// </para>
/// Crash safety: after <see cref="SendAsync"/> returns, inbox handlers are durably persisted.
/// If the channel message is lost (crash), the <see cref="InboxProcessor{TDbContext}"/> still picks up
/// the handler statuses on restart. If the outbox retries the send, inbox deduplication prevents double-delivery.
/// </summary>
internal class DurableLocalMessageSender<TDbContext>(
    Channel<LocalMessage> messageChannel,
    IServiceScopeFactory scopeFactory,
    InboxHandlerRegistry inboxHandlerRegistry,
    InboxProcessor<TDbContext> inboxProcessor,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<DurableLocalMessageSender<TDbContext>> logger)
    : IMessageSender
    where TDbContext : DbContext, IInboxDbContext
{
    public string TransportName => LocalTransportConstants.TransportName;

    public async Task SendAsync(byte[] content, MessageProperties props, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = LocalSendInstrumentation.StartSendActivity(props, content.Length);
        var transportMessage = LocalTransportMessageSnapshotFactory.Create(content, props);
        Exception? sendException = null;

        try
        {
            // Step 1: Write inbox-managed handlers to DB (crash-safe at this point)
            var inboxHandlers = props.Type != null
                ? inboxHandlerRegistry.GetByWireTypeName(props.Type)
                : Array.Empty<InboxHandlerRegistration>();

            if (inboxHandlers.Count > 0)
            {
                await PersistToInboxAsync(content, props, inboxHandlers, cancellationToken);
            }

            // Step 2: Write to in-memory channel so LocalTransportConsumer can process non-inbox handlers.
            // For inbox-only messages this is still done so MessageDispatcher runs (it will skip inbox handlers).
            await messageChannel.Writer.WriteAsync(new LocalMessage(content, props), cancellationToken);
        }
        catch (Exception ex)
        {
            sendException = ex;
            LocalSendInstrumentation.SetActivityError(activity, ex);
            throw;
        }
        finally
        {
            await LocalSendInstrumentation.RecordSendMetricsAndNotifyAsync(
                startTimestamp, sendException, props, content,
                TransportName, transportMessage, observers, timeProvider, logger);
        }
    }

    private async Task PersistToInboxAsync(
        byte[] content,
        MessageProperties props,
        IReadOnlyList<InboxHandlerRegistration> inboxHandlers,
        CancellationToken cancellationToken)
    {
        await InboxPersistence.PersistAsync<TDbContext>(
            scopeFactory, props.Id!, LocalTransportConstants.TransportName,
            content, props, inboxHandlers, timeProvider,
            observers, inboxProcessor.TriggerAsync, logger, cancellationToken);
    }
}
