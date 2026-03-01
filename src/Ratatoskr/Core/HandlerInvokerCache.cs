using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Ratatoskr.Core;

/// <summary>
/// Caches compiled delegates for invoking <see cref="IMessageHandler{TMessage}"/> implementations
/// without per-call reflection overhead.
/// </summary>
internal static class HandlerInvokerCache
{
    private static readonly ConcurrentDictionary<Type, Func<object, object, MessageProperties, CancellationToken, Task>> _cache = new();

    public static Func<object, object, MessageProperties, CancellationToken, Task> Get(Type messageType) =>
        _cache.GetOrAdd(messageType, CreateInvoker);

    private static Func<object, object, MessageProperties, CancellationToken, Task> CreateInvoker(Type messageType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");
        var propsParam = Expression.Parameter(typeof(MessageProperties), "props");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var interfaceType = typeof(IMessageHandler<>).MakeGenericType(messageType);
        var handleMethod = interfaceType.GetMethod(nameof(IMessageHandler<object>.HandleAsync))!;

        var typedHandler = Expression.Convert(handlerParam, interfaceType);
        var typedMessage = Expression.Convert(messageParam, messageType);
        var call = Expression.Call(typedHandler, handleMethod, typedMessage, propsParam, ctParam);

        return Expression.Lambda<Func<object, object, MessageProperties, CancellationToken, Task>>(
            call, handlerParam, messageParam, propsParam, ctParam).Compile();
    }
}
