using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.Core;

/// <summary>
/// Dispatches incoming messages to all registered handlers.
/// Supports multiple handlers per message type.
/// </summary>
public class MessageDispatcher(
    ChannelRegistry channelRegistry,
    IMessageSerializer deserializer,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<MessageDispatcher> logger)
{
    /// <summary>
    /// Dispatches a message to all registered handlers.
    /// All handlers run in the same DI scope.
    /// </summary>
    public async Task<DispatchResult> DispatchAsync(byte[] body, MessageProperties properties, CancellationToken cancellationToken, string? channelName = null)
    {
        if (properties.Type == null)
        {
            logger.LogError("Received message without a type");
            return DispatchResult.PermanentError;
        }

        // 1. Resolve Message Type
        Type? messageType = null;

        // Try ChannelRegistry first (Topology based)
        if (channelName != null)
        {
            var channel = channelRegistry.GetConsumeChannel(channelName);
            var msgReg = channel?.Messages.FirstOrDefault(m => m.MessageTypeName == properties.Type);
            if (msgReg != null)
            {
                messageType = msgReg.MessageType;
            }
        }

        // Try global lookup in ChannelRegistry if not found in channel (or channel not provided)
        if (messageType == null)
        {
             // Find any consumer channel that handles this type
             // If multiple, this is ambiguous, but we pick first for now
             var match = channelRegistry.FindConsumeChannelsForType(properties.Type).FirstOrDefault();
             if (match.Message != null)
             {
                 messageType = match.Message.MessageType;
             }
        }

        if (messageType == null)
        {
            logger.LogWarning("No registration found for event type '{EventType}'", properties.Type);
            return DispatchResult.NoHandlers;
        }

        // 2. Deserialize
        object? message;
        try
        {
            message = deserializer.Deserialize(body, messageType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deserialize message of type '{EventType}'", properties.Type);
            return DispatchResult.PermanentError;
        }
        if (message == null)
        {
            logger.LogError("Message of type '{EventType}' deserialized to null", properties.Type);
            return DispatchResult.PermanentError;
        }

        // 3. Dispatch to Handlers via DI
        List<Exception>? exceptions = null;
        using var scope = scopeFactory.CreateScope();

        // Generic handler interface type: IMessageHandler<T>
        var interfaceType = typeof(IMessageHandler<>).MakeGenericType(messageType);
        var handlersInstances = scope.ServiceProvider.GetServices(interfaceType);

        if (!handlersInstances.Any())
        {
             logger.LogWarning("No handlers registered in DI for CLR type '{Type}' (Event: {EventType})", messageType.Name, properties.Type);
             return DispatchResult.NoHandlers;
        }

        foreach (var handler in handlersInstances)
        {
            if (handler == null) continue;
            try
            {
                var handleMethod = interfaceType.GetMethod(nameof(IMessageHandler<object>.HandleAsync))!;
                await (Task)handleMethod.Invoke(handler, [message, properties, cancellationToken])!;

                logger.LogDebug("Handler '{Handler}' processed message '{Id}' of type '{Type}'",
                    handler.GetType().Name, properties.Id, properties.Type);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handler '{Handler}' failed for message '{Id}' of type '{Type}'",
                    handler.GetType().Name, properties.Id, properties.Type);
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        var result = exceptions != null ? DispatchResult.RecoverableError : DispatchResult.Success;

        foreach (var observer in observers)
        {
            try
            {
                await observer.OnMessageActivity(new MessageActivity
                {
                    Stage = MessageStage.Dispatched,
                    Properties = properties,
                    SerializedBody = body,
                    Message = message,
                    MessageType = messageType,
                    DispatchResult = result,
                    Exception = exceptions switch
                    {
                        null => null,
                        [var single] => single,
                        _ => new AggregateException(exceptions)
                    },
                    Timestamp = timeProvider.GetUtcNow(),
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Message activity observer failed at the {Stage} stage", MessageStage.Dispatched);
            }
        }

        return result;
    }
}

public enum DispatchResult
{
    Success,
    NoHandlers,
    RecoverableError,
    PermanentError,
}
