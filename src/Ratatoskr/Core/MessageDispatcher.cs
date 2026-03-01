using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ratatoskr.Core;

/// <summary>
/// Dispatches incoming messages to all registered handlers.
/// Supports multiple handlers per message type.
/// When inbox interceptors are registered, inbox-managed handlers are queued to durable storage
/// instead of being called synchronously.
/// </summary>
public class MessageDispatcher(
    ChannelRegistry channelRegistry,
    IMessageSerializer deserializer,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<MessageDispatcher> logger,
    InboxHandlerRegistry? inboxHandlerRegistry = null,
    IEnumerable<IInboxInterceptor>? inboxInterceptors = null)
{
    private readonly IReadOnlyList<IInboxInterceptor> _inboxInterceptors =
        inboxInterceptors?.ToArray() ?? Array.Empty<IInboxInterceptor>();

    /// <summary>
    /// Dispatches a message to all registered handlers.
    /// Non-inbox handlers run synchronously in the same DI scope.
    /// Inbox-managed handlers are queued to durable storage via the registered interceptor.
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

        // 3. Dispatch to Handlers via DI
        List<Exception>? exceptions = null;
        using var scope = scopeFactory.CreateScope();

        // Generic handler interface type: IMessageHandler<T>
        var interfaceType = typeof(IMessageHandler<>).MakeGenericType(messageType);
        var handlersInstances = scope.ServiceProvider.GetServices(interfaceType).ToArray();

        if (!handlersInstances.Any())
        {
             logger.LogWarning("No handlers registered in DI for CLR type '{Type}' (Event: {EventType})", messageType.Name, properties.Type);
             return DispatchResult.NoHandlers;
        }

        // 4. If inbox interceptors are registered, queue inbox-managed handlers to durable storage
        var inboxHandlers = inboxHandlerRegistry != null && _inboxInterceptors.Count > 0
            ? inboxHandlerRegistry.GetByMessageType(messageType)
            : (IReadOnlyList<InboxHandlerRegistration>)Array.Empty<InboxHandlerRegistration>();

        if (inboxHandlers.Count > 0)
        {
            try
            {
                var effectiveTransportName = transportName ?? "unknown";
                foreach (var interceptor in _inboxInterceptors)
                {
                    await interceptor.AcceptAsync(scope.ServiceProvider, body, properties, inboxHandlers, effectiveTransportName, cancellationToken);
                }
                logger.LogDebug("Queued {Count} inbox-managed handler(s) for message '{Id}' of type '{Type}'",
                    inboxHandlers.Count, properties.Id, properties.Type);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox interceptor failed for message '{Id}' of type '{Type}'", properties.Id, properties.Type);
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        // 5. Call non-inbox handlers synchronously
        var inboxHandlerTypes = new HashSet<Type>(inboxHandlers.Select(h => h.HandlerType));

        foreach (var handler in handlersInstances)
        {
            if (handler == null) continue;

            // Skip inbox-managed handlers — they will be delivered by InboxProcessor
            if (inboxHandlerTypes.Contains(handler.GetType()))
                continue;

            try
            {
                var invoke = HandlerInvokerCache.Get(messageType);
                await invoke(handler, message, properties, cancellationToken);

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

        DispatchResult result;
        if (exceptions != null)
        {
            result = DispatchResult.RecoverableError;
        }
        else if (inboxHandlers.Count > 0 && inboxHandlerTypes.Count == handlersInstances.Count(h => h != null))
        {
            // All registered handlers were inbox-managed; none called synchronously
            result = DispatchResult.Queued;
        }
        else
        {
            result = DispatchResult.Success;
        }

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

    /// <summary>
    /// All handlers for this message were inbox-managed and have been queued to durable storage.
    /// They will be delivered asynchronously by the InboxProcessor.
    /// Transports should treat this the same as <see cref="Success"/> (ack the message).
    /// </summary>
    Queued,
}
