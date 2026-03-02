using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.Local;

internal class LocalMessageSender(
    Channel<LocalMessage> messageChannel,
    LocalTelemetry telemetry,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<LocalMessageSender> logger)
    : IMessageSender
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

            await observers.NotifyAsync(new MessageSent
            {
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
