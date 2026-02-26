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
            "send local",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            props.TraceParent = activity.Id;
            props.TraceState = activity.TraceStateString;

            activity.SetTag(MessagingSemanticConventions.OperationName, "send");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend);
            activity.SetTag(MessagingSemanticConventions.System, "local");
            activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, content.Length);
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
            activity?.SetTag(MessagingSemanticConventions.ErrorType, ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            var tags = new TagList
            {
                { MessagingSemanticConventions.System, "local" },
                { MessagingSemanticConventions.OperationName, "send" },
                { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeSend },
            };

            if (sendException != null)
            {
                tags.Add(MessagingSemanticConventions.ErrorType, sendException.GetType().FullName);
            }

            RatatoskrDiagnostics.ClientOperationDuration.Record(duration, tags);
            RatatoskrDiagnostics.ClientSentMessages.Add(1, tags);

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
