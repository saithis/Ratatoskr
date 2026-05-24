using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;

namespace Ratatoskr;

public sealed class Ratatoskr(
    IMessageSerializerResolver serializerResolver,
    IEnumerable<IMessageSender> senders,
    IMessagePropertiesEnricher enricher,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<Ratatoskr> logger
) : IRatatoskr
{
    private readonly FrozenDictionary<string, IMessageSender> _senderMap =
        senders.ToFrozenDictionary(x => x.TransportName);
    private readonly IMessageActivityObserver[] _observers = [.. observers];

    /// <inheritdoc/>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "We catch all exceptions from one transport to allow trying other transports and notifying observers, and rethrow them combined as an AggregateException at the end."
    )]
    public async Task PublishDirectAsync<TMessage>(
        TMessage message,
        MessageProperties? props = null,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull
    {
        // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/
        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "publish",
            ActivityKind.Producer
        );

        props = enricher.Enrich<TMessage>(props);

        if (activity != null)
        {
            _ = activity.SetTag(MessagingSemanticConventions.OperationName, "publish");
            _ = activity.SetTag(
                MessagingSemanticConventions.OperationType,
                MessagingSemanticConventions.OperationTypeCreate
            );
            _ = activity.SetTag(MessagingSemanticConventions.System, "ratatoskr");
            _ = activity.SetTag(MessagingSemanticConventions.MessageId, props.Id);
        }

        var serializer = serializerResolver.GetSerializer(typeof(TMessage));
        var serializedMessage = serializer.Serialize(message);
        props.ContentType = serializer.ContentType;

        if (props.Transports.Count == 0)
        {
            throw new InvalidOperationException(
                $"No transport found for message '{typeof(TMessage)}'"
            );
        }

        List<Exception>? exceptions = null;
        var matchedAny = false;
        foreach (var transport in props.Transports)
        {
            if (!_senderMap.TryGetValue(transport, out var sender))
            {
                continue;
            }

            matchedAny = true;
            Exception? sendException = null;
            try
            {
                await sender
                    .SendAsync(serializedMessage, props, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogTransportSendFailed(logger, ex, sender.TransportName, props.Id);
                sendException = ex;
                exceptions ??= [];
                exceptions.Add(ex);
            }

            await _observers
                .NotifyAsync(
                    new MessageActivity
                    {
                        Stage = MessageStage.Published,
                        Properties = props,
                        SerializedBody = serializedMessage,
                        Message = message,
                        MessageType = typeof(TMessage),
                        TransportName = sender.TransportName,
                        Exception = sendException,
                        Timestamp = timeProvider.GetUtcNow(),
                    },
                    logger
                )
                .ConfigureAwait(false);
        }

        if (!matchedAny)
        {
            throw new InvalidOperationException(
                $"No transport found for message '{typeof(TMessage)}'"
            );
        }

        if (exceptions is { Count: > 0 })
        {
            _ = (
                activity?.SetTag(
                    MessagingSemanticConventions.ErrorType,
                    exceptions[0].GetType().FullName
                )
            );
            _ = (activity?.SetStatus(ActivityStatusCode.Error, exceptions[0].Message));
            throw new AggregateException(
                $"One or more transports failed to send message '{props.Id}'",
                exceptions
            );
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Transport '{TransportName}' failed to send message '{MessageId}'"
    )]
    private static partial void LogTransportSendFailed(
        ILogger logger,
        Exception ex,
        string transportName,
        string messageId
    );
}
