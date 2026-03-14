using RabbitMQ.Client;

namespace Ratatoskr.RabbitMq;

public class RabbitMqConnectionManager(RabbitMqOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _sendChannelLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _sendChannel;

    public async Task<IChannel> CreateChannelAsync(bool enablePublisherConfirms, CancellationToken cancellationToken = default)
    {
        var connection = await GetOrCreateConnectionAsync(cancellationToken);

        if (enablePublisherConfirms)
        {
            var options = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true
            );
            return await connection.CreateChannelAsync(options, cancellationToken);
        }

        return await connection.CreateChannelAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Returns a reusable AMQP channel for send operations. The channel is lazily created
    /// and automatically recreated if the previous one was closed.
    /// Callers must NOT dispose the returned channel.
    /// </summary>
    public async Task<IChannel> GetOrCreateSendChannelAsync(bool enablePublisherConfirms, CancellationToken cancellationToken = default)
    {
        if (_sendChannel is { IsOpen: true })
            return _sendChannel;

        await _sendChannelLock.WaitAsync(cancellationToken);
        try
        {
            if (_sendChannel is { IsOpen: true })
                return _sendChannel;

            if (_sendChannel != null)
            {
                _sendChannel.Dispose();
                _sendChannel = null;
            }

            var connection = await GetOrCreateConnectionAsync(cancellationToken);

            if (enablePublisherConfirms)
            {
                var channelOptions = new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true
                );
                _sendChannel = await connection.CreateChannelAsync(channelOptions, cancellationToken);
            }
            else
            {
                _sendChannel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            }

            return _sendChannel;
        }
        finally
        {
            _sendChannelLock.Release();
        }
    }

    private async Task<IConnection> GetOrCreateConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            var factory = CreateConnectionFactory();
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private ConnectionFactory CreateConnectionFactory()
    {
        if (options.ConnectionString is null)
            throw new InvalidOperationException(
                "RabbitMQ connection string is not configured. Set RabbitMqOptions.ConnectionString.");

        return new ConnectionFactory { Uri = options.ConnectionString };
    }

    public async ValueTask DisposeAsync()
    {
        if (_sendChannel != null)
        {
            if (_sendChannel.IsOpen)
                await _sendChannel.CloseAsync();
            _sendChannel.Dispose();
            _sendChannel = null;
        }
        if (_connection != null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
            _connection = null;
        }
        _sendChannelLock.Dispose();
        _connectionLock.Dispose();
    }
}
