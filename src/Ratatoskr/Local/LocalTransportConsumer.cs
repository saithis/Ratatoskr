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
    private readonly CancellationTokenSource _drainProcessingCts = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting local transport consumer");

        await foreach (var message in messageChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await ProcessMessageAsync(message, _drainProcessingCts.Token);
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

        var transportMessage = LocalTransportMessageSnapshotFactory.Create(message.Content, message.Properties);

        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(new MessageActivity
                {
                    Stage = MessageStage.Received,
                    Properties = message.Properties,
                    SerializedBody = message.Content,
                    TransportName = LocalTransportConstants.TransportName,
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
            { MessagingSemanticConventions.System, "local" },
            { MessagingSemanticConventions.OperationName, "process" },
            { MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeProcess },
        };

        RatatoskrDiagnostics.ClientConsumedMessages.Add(1, tags);

        if (message.Properties.Time.HasValue)
        {
            var receiveLag = (receivedTimestamp - message.Properties.Time.Value).TotalSeconds;
            RatatoskrDiagnostics.ReceiveLag.Record(receiveLag, tags);
        }

        // Restore trace context from message
        ActivityContext.TryParse(
            message.Properties.TraceParent,
            message.Properties.TraceState,
            out var parentContext);

        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "process local",
            ActivityKind.Consumer,
            parentContext);

        if (activity != null)
        {
            activity.SetTag(MessagingSemanticConventions.OperationName, "process");
            activity.SetTag(MessagingSemanticConventions.OperationType, MessagingSemanticConventions.OperationTypeProcess);
            activity.SetTag(MessagingSemanticConventions.System, "local");
            activity.SetTag(MessagingSemanticConventions.MessageId, message.Properties.Id);
            activity.SetTag(MessagingSemanticConventions.MessageBodySize, message.Content.Length);
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        var result = await dispatcher.DispatchAsync(
            message.Content, message.Properties, cancellationToken);
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        var errorType = result switch
        {
            DispatchResult.Success => (string?)null,
            DispatchResult.NoHandlers => "NoHandlerError",
            _ => "ProcessingError"
        };

        if (errorType != null)
        {
            tags.Add(MessagingSemanticConventions.ErrorType, errorType);
        }

        RatatoskrDiagnostics.ProcessDuration.Record(duration, tags);

        if (message.Properties.Time.HasValue)
        {
            var processLag = (timeProvider.GetUtcNow() - message.Properties.Time.Value).TotalSeconds;
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
            await _drainProcessingCts.CancelAsync();
        }
    }
}
