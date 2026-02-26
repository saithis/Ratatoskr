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

        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "Ratatoskr.Send",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag("messaging.system", "local");
            activity.SetTag("messaging.message.id", props.Id);
            activity.SetTag("messaging.message.body.size", content.Length);
        }

        var transportMessage = LocalTransportMessageSnapshotFactory.Create(content, props);

        Exception? sendException = null;

        try
        {
            await messageChannel.Writer.WriteAsync(new LocalMessage(content, props), cancellationToken);
        }
        catch (Exception ex)
        {
            sendException = ex;
            throw;
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var tags = new TagList
            {
                { "messaging.system", "local" },
            };

            RatatoskrDiagnostics.PublishDuration.Record(duration, tags);
            RatatoskrDiagnostics.PublishMessages.Add(1, tags);

            var sentTimestamp = timeProvider.GetUtcNow();

            foreach (var observer in observers)
            {
                try
                {
                    await observer.OnMessageActivity(new MessageActivity
                    {
                        Stage = MessageStage.Sent,
                        Properties = props,
                        SerializedBody = content,
                        TransportName = TransportName,
                        TransportMessage = transportMessage,
                        Exception = sendException,
                        Timestamp = sentTimestamp,
                    });
                }
                catch
                {
                    // Observer failures must not affect the pipeline
                }
            }
        }
    }
}
