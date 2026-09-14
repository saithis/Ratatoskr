using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Ratatoskr.Core;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq.Management;

/// <summary>
/// Resolves live queue depths and DLQ information for RabbitMQ channels.
/// </summary>
public sealed class RabbitMqChannelQueueResolver(
    ChannelRegistry channelRegistry,
    RabbitMqConnectionManager connectionManager
) : IChannelQueueResolver, IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Roslynator",
        "RCS1075:Avoid empty catch clause that catches System.Exception",
        Justification = "If the broker is unreachable or initializing, reporting 0 allows announcement to succeed."
    )]
    public async Task<IReadOnlyList<QueueTopology>> ResolveQueuesAsync(
        string channelName,
        ChannelIntent intent,
        IReadOnlyList<string> messageTypes,
        CancellationToken cancellationToken = default
    )
    {
        if (intent != ChannelIntent.Consume)
        {
            return [];
        }

        var channelReg = channelRegistry
            .GetConsumeChannels()
            .FirstOrDefault(c => string.Equals(c.ChannelName, channelName, StringComparison.Ordinal));

        if (channelReg is null)
        {
            return [];
        }

        var channelOpts = channelReg.GetRabbitMqChannelOptions();
        if (channelOpts is null)
        {
            return [];
        }

        var queueName = channelOpts.QueueName ?? channelReg.ChannelName;
        var hasDlq = channelOpts.Retry.UseManaged;
        var dlqName = hasDlq ? $"{queueName}{channelOpts.Retry.DeadLetterSuffix}" : null;

        long queueCount = 0;
        long dlqCount = 0;

        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_channel is not { IsOpen: true })
                {
                    if (_channel is not null)
                    {
                        await _channel.DisposeAsync().AsTask()
                            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        _channel = null;
                    }

                    _channel = await connectionManager.CreateChannelAsync(
                        enablePublisherConfirms: false,
                        cancellationToken
                    );
                }

                queueCount = await SafeMessageCountAsync(_channel, queueName, cancellationToken);
                if (hasDlq && dlqName is not null)
                {
                    dlqCount = await SafeMessageCountAsync(_channel, dlqName, cancellationToken);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // If the broker is unreachable or initializing, reporting 0 allows announcement to succeed
        }

        return [new QueueTopology(queueName, queueCount, dlqName, dlqCount)];
    }

    private static async Task<long> SafeMessageCountAsync(
        IChannel channel,
        string queueName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await channel.MessageCountAsync(queueName, cancellationToken);
        }
        catch (OperationInterruptedException)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().AsTask()
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _channel = null;
        }

        _gate.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_channel is not null)
        {
            _channel.Dispose();
            _channel = null;
        }

        _gate.Dispose();
    }
}
