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
    public string TransportName => "local";

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

        var transportMessage = CreateTransportMessage(content, props);

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

    private static TransportMessage CreateTransportMessage(byte[] content, MessageProperties props)
    {
        var headers = new Dictionary<string, object?>();

        if (props.ContentType != null) headers["content-type"] = props.ContentType;
        if (props.Id != null) headers["message-id"] = props.Id;
        if (props.Type != null) headers["type"] = props.Type;
        if (props.Source != null) headers["source"] = props.Source;
        if (props.TraceParent != null) headers["traceparent"] = props.TraceParent;
        if (props.TraceState != null) headers["tracestate"] = props.TraceState;

        foreach (var header in props.Headers)
        {
            headers[header.Key] = header.Value;
        }

        return new TransportMessage
        {
            Body = content,
            Headers = headers,
            Metadata = new Dictionary<string, object?>
            {
                ["transport"] = "local",
            },
        };
    }
}
