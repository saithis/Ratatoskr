using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.Core;

/// <summary>
/// Dispatches incoming messages to registered fire-and-forget handlers for the given channel.
/// Uses <see cref="ChannelHandlerRegistry"/> for handler lookup instead of DI discovery.
/// </summary>
public class MessageDispatcher(
    ChannelRegistry channelRegistry,
    ChannelHandlerRegistry channelHandlerRegistry,
    IMessageSerializer deserializer,
    HandlerInvoker handlerInvoker,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<MessageDispatcher> logger)
{
    /// <summary>
    /// Dispatches a message to all registered fire-and-forget handlers for the channel, each in its own DI scope.
    /// </summary>
    public async Task<DispatchResult> DispatchAsync(byte[] body, MessageProperties properties, CancellationToken cancellationToken, string channelName, string transportName)
    {
        if (properties.Type == null)
        {
            logger.LogError("Received message without a type");
            return DispatchResult.PermanentError;
        }

        // 1. Resolve Message Type
        Type? messageType = null;

        var channel = channelRegistry.GetConsumeChannel(channelName);
        var msgReg = channel?.Messages.FirstOrDefault(m => m.MessageTypeName == properties.Type);
        if (msgReg != null)
        {
            messageType = msgReg.MessageType;
        }

        if (messageType == null)
        {
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

        // 3. Get fire-and-forget handlers from the channel handler registry
        var handlers = channelHandlerRegistry.GetFireAndForgetHandlers(channelName, messageType);

        if (handlers.Count == 0)
        {
            logger.LogDebug("No fire-and-forget handlers for '{Type}' on channel '{Channel}'", properties.Type, channelName);
            return DispatchResult.NoHandlers;
        }

        // 4. Invoke each handler in its own DI scope for full isolation.
        List<Exception>? exceptions = null;
        var invoked = 0;

        foreach (var handler in handlers)
        {
            try
            {
                await handlerInvoker.InvokeAsync(handler.HandlerType, message, properties, cancellationToken);
                invoked++;

                logger.LogDebug("Handler '{Handler}' processed message '{Id}' of type '{Type}'",
                    handler.HandlerType.Name, properties.Id, properties.Type);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handler '{Handler}' failed for message '{Id}' of type '{Type}'",
                    handler.HandlerType.Name, properties.Id, properties.Type);
                invoked++;
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        DispatchResult result;
        if (exceptions != null)
            result = DispatchResult.RecoverableError;
        else if (invoked == 0)
            result = DispatchResult.NoHandlers;
        else
            result = DispatchResult.Success;

        await observers.NotifyAsync(new MessageActivity
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
        }, logger);

        return result;
    }
}

/// <summary>Outcome of message dispatch.</summary>
public enum DispatchResult
{
    /// <summary>All handlers completed successfully.</summary>
    Success,
    /// <summary>No handlers found for the message.</summary>
    NoHandlers,
    /// <summary>One or more handlers failed but may succeed on retry.</summary>
    RecoverableError,
    /// <summary>Message could not be processed (deserialization failure, etc.).</summary>
    PermanentError,
}
