using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.Core;

/// <summary>
/// Dispatches incoming messages to registered fire-and-forget handlers for the given channel.
/// Uses <see cref="ChannelHandlerRegistry"/> for handler lookup instead of DI discovery.
/// </summary>
public sealed partial class MessageDispatcher(
    ChannelRegistry channelRegistry,
    ChannelHandlerRegistry channelHandlerRegistry,
    IMessageSerializerResolver serializerResolver,
    HandlerInvoker handlerInvoker,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<MessageDispatcher> logger
)
{
    private readonly IMessageActivityObserver[] _observers = [.. observers];

    /// <summary>
    /// Dispatches a message to all registered fire-and-forget handlers for the channel, each in its own DI scope.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Exceptions are caught during deserialization and handler execution to log them and return a recoverable or permanent dispatch result instead of crashing the consumer process."
    )]
    public async Task<DispatchResult> DispatchAsync(
        byte[] body,
        MessageProperties properties,
        string channelName,
        string transportName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(properties);

        using var activity = RatatoskrDiagnostics.ActivitySource.StartActivity(
            "dispatch",
            ActivityKind.Consumer
        );
        if (activity != null)
        {
            _ = activity.SetTag(MessagingSemanticConventions.OperationName, "dispatch");
            _ = activity.SetTag(
                MessagingSemanticConventions.OperationType,
                MessagingSemanticConventions.OperationTypeProcess
            );
            _ = activity.SetTag(MessagingSemanticConventions.System, "ratatoskr");
            _ = activity.SetTag(MessagingSemanticConventions.DestinationName, channelName);
            _ = activity.SetTag(MessagingSemanticConventions.MessageId, properties.Id);
        }

        if (properties.Type == null)
        {
            LogReceivedMessageWithoutType(logger);
            _ = (activity?.SetStatus(ActivityStatusCode.Error, "Message has no type"));
            return DispatchResult.PermanentError;
        }

        // 1. Resolve Message Type
        Type? messageType = null;

        var channel = channelRegistry.GetConsumeChannel(channelName);
        var msgReg = channel?.GetMessage(properties.Type);
        if (msgReg != null)
        {
            messageType = msgReg.MessageType;
        }

        if (messageType == null)
        {
            var match = channelRegistry
                .FindConsumeChannelsForType(properties.Type)
                .FirstOrDefault();
            if (match.Message != null)
            {
                messageType = match.Message.MessageType;
            }
        }

        if (messageType == null)
        {
            LogNoRegistrationFound(logger, properties.Type);
            _ = (
                activity?.SetStatus(
                    ActivityStatusCode.Error,
                    $"No registration found for event type '{properties.Type}'"
                )
            );
            return DispatchResult.NoHandlers;
        }

        // 2. Deserialize
        object? message;
        try
        {
            var serializer = serializerResolver.GetSerializer(messageType);
            message = serializer.Deserialize(body, messageType);
        }
        catch (Exception ex)
        {
            LogDeserializationFailed(logger, ex, properties.Type);
            _ = (activity?.SetTag(MessagingSemanticConventions.ErrorType, ex.GetType().FullName));
            _ = (activity?.SetStatus(ActivityStatusCode.Error, ex.Message));
            return DispatchResult.PermanentError;
        }
        if (message == null)
        {
            LogDeserializedToNull(logger, properties.Type);
            _ = (
                activity?.SetStatus(
                    ActivityStatusCode.Error,
                    $"Message of type '{properties.Type}' deserialized to null"
                )
            );
            return DispatchResult.PermanentError;
        }

        // 3. Get fire-and-forget handlers from the channel handler registry
        var handlers = channelHandlerRegistry.GetFireAndForgetHandlers(channelName, messageType);

        if (handlers.Count == 0)
        {
            LogNoHandlersFound(logger, properties.Type, channelName);
            return DispatchResult.NoHandlers;
        }

        // 4. Invoke each handler in its own DI scope for full isolation.
        List<Exception>? exceptions = null;

        foreach (var handler in handlers)
        {
            try
            {
                await handlerInvoker.InvokeAsync(
                    handler.HandlerType,
                    message,
                    properties,
                    cancellationToken
                );

                LogHandlerProcessed(
                    logger,
                    handler.HandlerType.Name,
                    properties.Id,
                    properties.Type
                );
            }
            catch (Exception ex)
            {
                LogHandlerFailed(
                    logger,
                    ex,
                    handler.HandlerType.Name,
                    properties.Id,
                    properties.Type
                );
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        var result = exceptions != null ? DispatchResult.RecoverableError : DispatchResult.Success;

        if (result == DispatchResult.RecoverableError && activity != null)
        {
            _ = activity.SetTag(
                MessagingSemanticConventions.ErrorType,
                exceptions!.Count == 1
                    ? exceptions[0].GetType().FullName
                    : typeof(AggregateException).FullName
            );
            _ = activity.SetStatus(
                ActivityStatusCode.Error,
                exceptions.Count == 1
                    ? exceptions[0].Message
                    : $"Multiple handlers failed ({exceptions.Count} errors)"
            );
        }

        await _observers.NotifyAsync(
            new MessageActivity
            {
                Stage = MessageStage.Dispatched,
                Properties = properties,
                SerializedBody = body,
                Message = message,
                MessageType = messageType,
                DispatchResult = result,
                TransportName = transportName,
                Exception = exceptions switch
                {
                    null => null,
                    [var single] => single,
                    _ => new AggregateException(exceptions),
                },
                Timestamp = timeProvider.GetUtcNow(),
            },
            logger
        );

        return result;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Received message without a type"
    )]
    private static partial void LogReceivedMessageWithoutType(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "No registration found for event type '{EventType}'"
    )]
    private static partial void LogNoRegistrationFound(ILogger logger, string eventType);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Error,
        Message = "Failed to deserialize message of type '{EventType}'"
    )]
    private static partial void LogDeserializationFailed(
        ILogger logger,
        Exception ex,
        string eventType
    );

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Message of type '{EventType}' deserialized to null"
    )]
    private static partial void LogDeserializedToNull(ILogger logger, string eventType);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "No fire-and-forget handlers for '{Type}' on channel '{Channel}'"
    )]
    private static partial void LogNoHandlersFound(ILogger logger, string type, string channel);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Debug,
        Message = "Handler '{Handler}' processed message '{Id}' of type '{Type}'"
    )]
    private static partial void LogHandlerProcessed(
        ILogger logger,
        string handler,
        string? id,
        string type
    );

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Error,
        Message = "Handler '{Handler}' failed for message '{Id}' of type '{Type}'"
    )]
    private static partial void LogHandlerFailed(
        ILogger logger,
        Exception ex,
        string handler,
        string? id,
        string type
    );
}

/// <summary>Outcome of message dispatch.</summary>
public enum DispatchResult
{
    /// <summary>All handlers completed successfully.</summary>
    Success = 0,

    /// <summary>No handlers found for the message.</summary>
    NoHandlers = 1,

    /// <summary>One or more handlers failed but may succeed on retry.</summary>
    RecoverableError = 2,

    /// <summary>Message could not be processed (deserialization failure, etc.).</summary>
    PermanentError = 3,
}
