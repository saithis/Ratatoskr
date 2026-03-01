using System.Diagnostics;
using System.Threading.Channels;
using Ratatoskr.Core;

namespace Ratatoskr.Local;

internal class LocalMessageSender(
    Channel<LocalMessage> messageChannel,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers)
    : IMessageSender
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
                TransportName, transportMessage, observers, timeProvider);
        }
    }
}
