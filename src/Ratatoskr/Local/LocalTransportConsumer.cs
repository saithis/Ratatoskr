using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.Local;

internal class LocalTransportConsumer(
    Channel<LocalMessage> messageChannel,
    MessageDispatcher dispatcher,
    TimeProvider timeProvider,
    LocalTransportOptions options,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<LocalTransportConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting local transport consumer");

        await foreach (var message in messageChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessMessageAsync(message, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error processing local message '{MessageId}'",
                    message.Properties.Id);
            }
        }

        logger.LogInformation("Local transport consumer stopped");
    }

    private async Task ProcessMessageAsync(LocalMessage message, CancellationToken cancellationToken)
    {
        var receivedTimestamp = timeProvider.GetUtcNow();

        var transportMessage = CreateTransportMessage(message);

        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(new MessageActivity
                {
                    Stage = MessageStage.Received,
                    Properties = message.Properties,
                    SerializedBody = message.Content,
                    TransportMessage = transportMessage,
                    Timestamp = receivedTimestamp,
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Message activity observer failed at {Stage}",
                    MessageStage.Received);
            }
        }

        var tags = new TagList
        {
            { "messaging.system", "local" },
        };

        RatatoskrDiagnostics.ReceiveMessages.Add(1, tags);

        if (message.Properties.Time.HasValue)
        {
            var receiveLag = (receivedTimestamp - message.Properties.Time.Value).TotalMilliseconds;
            RatatoskrDiagnostics.ReceiveLag.Record(receiveLag, tags);
        }

        // Restore trace context from message
        ActivityContext.TryParse(
            message.Properties.TraceParent,
            message.Properties.TraceState,
            out var parentContext);

        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "Ratatoskr.Receive",
            ActivityKind.Consumer,
            parentContext);

        if (activity != null)
        {
            activity.SetTag("messaging.system", "local");
            activity.SetTag("messaging.message.id", message.Properties.Id);
            activity.SetTag("messaging.message.body.size", message.Content.Length);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        var result = await dispatcher.DispatchAsync(
            message.Content, message.Properties, cancellationToken);
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        tags.Add("outcome", result switch
        {
            DispatchResult.Success => "success",
            DispatchResult.NoHandlers => "no_handler",
            _ => "failure"
        });

        RatatoskrDiagnostics.ProcessDuration.Record(duration, tags);
        RatatoskrDiagnostics.ProcessMessages.Add(1, tags);

        if (message.Properties.Time.HasValue)
        {
            var processLag = (timeProvider.GetUtcNow() - message.Properties.Time.Value).TotalMilliseconds;
            RatatoskrDiagnostics.ProcessLag.Record(processLag, tags);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping local transport consumer, draining queue...");
        messageChannel.Writer.Complete();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.ShutdownTimeout);

        try
        {
            await base.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Local transport consumer shutdown timed out");
        }
    }

    private static TransportMessage CreateTransportMessage(LocalMessage message)
    {
        var headers = new Dictionary<string, object?>();

        if (message.Properties.ContentType != null) headers["content-type"] = message.Properties.ContentType;
        if (message.Properties.Id != null) headers["message-id"] = message.Properties.Id;
        if (message.Properties.Type != null) headers["type"] = message.Properties.Type;
        if (message.Properties.Source != null) headers["source"] = message.Properties.Source;
        if (message.Properties.TraceParent != null) headers["traceparent"] = message.Properties.TraceParent;
        if (message.Properties.TraceState != null) headers["tracestate"] = message.Properties.TraceState;

        foreach (var header in message.Properties.Headers)
        {
            headers[header.Key] = header.Value;
        }

        return new TransportMessage
        {
            Body = message.Content,
            Headers = headers,
            Metadata = new Dictionary<string, object?>
            {
                ["transport"] = "local",
            },
        };
    }
}
