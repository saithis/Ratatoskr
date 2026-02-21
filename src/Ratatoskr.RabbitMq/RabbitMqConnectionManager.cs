using RabbitMQ.Client;

namespace Ratatoskr.RabbitMq;

public class RabbitMqConnectionManager(RabbitMqOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;

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
        if (_connection != null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
            _connection = null;
        }
        _connectionLock.Dispose();
    }
}
