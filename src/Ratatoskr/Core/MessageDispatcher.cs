using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.Core;

/// <summary>
/// Dispatches incoming messages to all registered handlers.
/// Supports multiple handlers per message type, each invoked in its own DI scope.
/// Handlers excluded by <see cref="IHandlerFilter"/> are skipped — they are
/// delivered separately by external infrastructure (e.g. the inbox processor).
/// </summary>
public class MessageDispatcher(
    ChannelRegistry channelRegistry,
    IMessageSerializer deserializer,
    HandlerInvoker handlerInvoker,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<MessageDispatcher> logger,
    IHandlerFilter? handlerFilter = null)
{
    /// <summary>
    /// Dispatches a message to all registered handlers, each in its own DI scope.
    /// </summary>
    public async Task<DispatchResult> DispatchAsync(byte[] body, MessageProperties properties, CancellationToken cancellationToken, string? channelName = null, string? transportName = null)
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

        // 3. Discover registered handler types via a short-lived DI scope.
        //    Each handler will later be invoked in its own scope for full isolation.
        List<Exception>? exceptions = null;
        var interfaceType = typeof(IMessageHandler<>).MakeGenericType(messageType);
        Type[] handlerTypes;
        using (var discoveryScope = scopeFactory.CreateScope())
        {
            handlerTypes = discoveryScope.ServiceProvider
                .GetServices(interfaceType)
                .Where(h => h != null)
                .Select(h => h!.GetType())
                .ToArray();
        }

        if (handlerTypes.Length == 0)
        {
             logger.LogWarning("No handlers registered in DI for CLR type '{Type}' (Event: {EventType})", messageType.Name, properties.Type);
             return DispatchResult.NoHandlers;
        }

        // 4. Invoke each handler in its own DI scope for full isolation.
        //    Skip handlers excluded by the handler filter (e.g. inbox-managed handlers).
        var invoked = 0;
        foreach (var handlerType in handlerTypes)
        {
            if (handlerFilter?.ShouldSkip(handlerType, messageType) == true)
                continue;

            try
            {
                await handlerInvoker.InvokeAsync(handlerType, message, properties, cancellationToken);
                invoked++;

                logger.LogDebug("Handler '{Handler}' processed message '{Id}' of type '{Type}'",
                    handlerType.Name, properties.Id, properties.Type);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handler '{Handler}' failed for message '{Id}' of type '{Type}'",
                    handlerType.Name, properties.Id, properties.Type);
                invoked++;
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        DispatchResult result;
        if (exceptions != null)
            result = DispatchResult.RecoverableError;
        else if (invoked == 0 && handlerTypes.Length > 0)
            result = DispatchResult.NoHandlers; // All handlers were filtered out
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

public enum DispatchResult
{
    Success,
    NoHandlers,
    RecoverableError,
    PermanentError,
}
