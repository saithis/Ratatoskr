using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr.Local;

internal class LocalTransportConsumer(
    Channel<LocalMessage> messageChannel,
    MessageDispatcher dispatcher,
    LocalTelemetry telemetry,
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

        await observers.NotifyAsync(new MessageActivity
        {
            Stage = MessageStage.Received,
            Properties = message.Properties,
            SerializedBody = message.Content,
            TransportName = LocalTransportConstants.TransportName,
            TransportMessage = transportMessage,
            Timestamp = receivedTimestamp,
        }, logger);

        telemetry.RecordReceived(message.Properties.Time, receivedTimestamp);

        using var activity = telemetry.StartConsumeActivity(message.Properties, message.Content.Length);

        var startTimestamp = Stopwatch.GetTimestamp();
        var result = await dispatcher.DispatchAsync(
            message.Content, message.Properties, cancellationToken);

        var errorType = result switch
        {
            DispatchResult.Success => (string?)null,
            DispatchResult.NoHandlers => "NoHandlerError",
            _ => "ProcessingError"
        };

        if (errorType != null)
        {
            activity?.SetTag(MessagingSemanticConventions.ErrorType, errorType);
            activity?.SetStatus(ActivityStatusCode.Error, errorType);
        }

        telemetry.RecordProcessed(startTimestamp, message.Properties.Time, errorType);
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
