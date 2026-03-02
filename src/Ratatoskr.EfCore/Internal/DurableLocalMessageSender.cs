using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.Local;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Durable replacement for <see cref="LocalMessageSender"/> when the inbox pattern is configured
/// alongside the local transport.
/// <para>
/// Delegates inbox persistence to <see cref="InboxAcceptor{TDbContext}"/> before writing to the
/// in-memory channel. This ensures crash safety: if the process dies after the DB write but before
/// the channel write, <see cref="InboxProcessor{TDbContext}"/> still picks up the handler statuses
/// on restart.
/// </para>
/// </summary>
internal class DurableLocalMessageSender<TDbContext>(
    Channel<LocalMessage> messageChannel,
    IInboxAcceptor inboxAcceptor,
    LocalTelemetry telemetry,
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
        using var activity = telemetry.StartSendActivity(props, content.Length);
        var transportMessage = LocalTransportMessageSnapshotFactory.Create(content, props);
        Exception? sendException = null;

        try
        {
            // Step 1: Persist inbox-managed handlers to DB (crash-safe at this point).
            await inboxAcceptor.AcceptAsync(content, props, LocalTransportConstants.TransportName, cancellationToken);

            // Step 2: Write to in-memory channel so LocalTransportConsumer dispatches non-inbox handlers.
            await messageChannel.Writer.WriteAsync(new LocalMessage(content, props), cancellationToken);
        }
        catch (Exception ex)
        {
            sendException = ex;
            LocalTelemetry.SetActivityError(activity, ex);
            throw;
        }
        finally
        {
            telemetry.RecordSent(startTimestamp, sendException);

            await observers.NotifyAsync(new MessageActivity
            {
                Stage = MessageStage.Sent,
                Properties = props,
                SerializedBody = content,
                TransportName = TransportName,
                TransportMessage = transportMessage,
                Exception = sendException,
                Timestamp = timeProvider.GetUtcNow(),
            }, logger);
        }
    }
}
