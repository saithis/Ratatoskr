using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ratatoskr.Core;
using Ratatoskr.EfCore;

namespace Ratatoskr;

internal sealed partial class RatatoskrClient(
    IServiceProvider serviceProvider,
    IMessageSerializerResolver serializerResolver,
    IEnumerable<IMessageSender> senders,
    IMessagePropertiesEnricher enricher,
    TimeProvider timeProvider,
    IEnumerable<IMessageActivityObserver> observers,
    ILogger<RatatoskrClient> logger
) : IRatatoskr
{
    private readonly FrozenDictionary<string, IMessageSender> _senderMap =
        senders.ToFrozenDictionary(x => x.TransportName);
    private readonly IMessageActivityObserver[] _observers = [.. observers];

    /// <inheritdoc/>
    /// <inheritdoc/>
    public Task PublishAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull
    {
        var dbContext = serviceProvider.GetService<IRatatoskrDbContext>();
        if (dbContext == null)
        {
            throw new InvalidOperationException(
                "Cannot stage message in outbox: No IRatatoskrDbContext is registered in the current scope. " +
                "Configure a default DbContext via services.AddRatatoskr<TDbContext>() or use PublishDirectAsync for direct publishing."
            );
        }

        dbContext.Outbox.Add(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishAsync<TMessage>(
        TMessage message,
        Action<PublishOptions> configure,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);
        var dbContext = serviceProvider.GetService<IRatatoskrDbContext>();
        if (dbContext == null)
        {
            throw new InvalidOperationException(
                "Cannot stage message in outbox: No IRatatoskrDbContext is registered in the current scope. " +
                "Configure a default DbContext via services.AddRatatoskr<TDbContext>() or use PublishDirectAsync for direct publishing."
            );
        }

        dbContext.Outbox.Add(message, configure);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishAsync<TMessage>(
        TMessage message,
        MessageProperties props,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(props);
        var dbContext = serviceProvider.GetService<IRatatoskrDbContext>();
        if (dbContext == null)
        {
            throw new InvalidOperationException(
                "Cannot stage message in outbox: No IRatatoskrDbContext is registered in the current scope. " +
                "Configure a default DbContext via services.AddRatatoskr<TDbContext>() or use PublishDirectAsync for direct publishing."
            );
        }

        dbContext.Outbox.Add(message, props);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishAsync<TDbContext, TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
        where TDbContext : DbContext, IRatatoskrDbContext
        where TMessage : notnull
    {
        var dbContext = serviceProvider.GetRequiredService<TDbContext>();
        dbContext.Outbox.Add(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishAsync<TDbContext, TMessage>(
        TMessage message,
        Action<PublishOptions> configure,
        CancellationToken cancellationToken = default
    )
        where TDbContext : DbContext, IRatatoskrDbContext
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);
        var dbContext = serviceProvider.GetRequiredService<TDbContext>();
        dbContext.Outbox.Add(message, configure);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishAsync<TDbContext, TMessage>(
        TMessage message,
        MessageProperties props,
        CancellationToken cancellationToken = default
    )
        where TDbContext : DbContext, IRatatoskrDbContext
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(props);
        var dbContext = serviceProvider.GetRequiredService<TDbContext>();
        dbContext.Outbox.Add(message, props);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PublishDirectAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken
    )
        where TMessage : notnull =>
        PublishDirectAsync(message, (Action<PublishOptions>?)null, cancellationToken);

    /// <inheritdoc/>
    public Task PublishDirectAsync<TMessage>(
        TMessage message,
        Action<PublishOptions>? configure,
        CancellationToken cancellationToken = default
    )
        where TMessage : notnull
    {
        PublishOptions? options = null;
        if (configure != null)
        {
            options = new PublishOptions();
            configure(options);
        }

        return PublishDirectAsync(message, options?.Properties, cancellationToken);
    }

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
        if (props?.ScheduledAt is { } scheduledAt)
        {
            var now = timeProvider.GetUtcNow();
            var delay = scheduledAt - now;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
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
                await sender.SendAsync(serializedMessage, props, cancellationToken);
            }
            catch (Exception ex)
            {
                LogTransportSendFailed(logger, ex, sender.TransportName, props.Id);
                sendException = ex;
                exceptions ??= [];
                exceptions.Add(ex);
            }

            await _observers.NotifyAsync(
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
            );
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
        string? messageId
    );
}
