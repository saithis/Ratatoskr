using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Ratatoskr.Management.RabbitMq;

/// <summary>
/// The control plane's own AMQP connection, independent of the application's messaging connection.
/// </summary>
/// <remarks>
/// Automatic connection and topology recovery are on: the client re-declares what it declared and
/// re-binds what it bound after a reconnect. The declare routine is still re-run idempotently on
/// recovery, because exclusive queues do not survive a disconnect and the process that owned them
/// has to ask for them again.
/// </remarks>
internal sealed class RabbitMqManagementConnection(string transportName, RabbitMqManagementOptions options)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    /// <summary>The transport name this connection belongs to, used in log and error messages.</summary>
    public string TransportName { get; } = transportName;

    /// <summary>Whether a connection is currently open.</summary>
    public bool IsConnected => _connection is { IsOpen: true };

    /// <summary>Raised when the connection drops, so callers can fail pending work deterministically.</summary>
    public event EventHandler? ConnectionLost;

    /// <summary>Opens a channel, optionally with publisher confirms.</summary>
    /// <remarks>
    /// Callers own one channel each. Publishing to a receiver-owned exchange that does not exist
    /// yet closes the channel, and that must never take down command consumption or in-flight
    /// replies — so heartbeats, replies and consumption never share one.
    /// <para>
    /// A channel that both consumes and publishes is also a throughput trap: consumer dispatch and
    /// the wait for a publisher confirm run on the same channel, so a burst of concurrent requests
    /// can stall behind its own replies. Publishing and consuming therefore get separate channels
    /// even where a single one would be legal.
    /// </para>
    /// </remarks>
    public async Task<IChannel> CreateChannelAsync(
        bool publisherConfirms,
        ushort? consumerDispatchConcurrency = null,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await GetOrCreateAsync(cancellationToken);

        return await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: publisherConfirms,
                publisherConfirmationTrackingEnabled: publisherConfirms,
                consumerDispatchConcurrency: consumerDispatchConcurrency
            ),
            cancellationToken
        );
    }

    private async Task<IConnection> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } opened)
            {
                return opened;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                Uri =
                    options.ConnectionString
                    ?? throw new InvalidOperationException(
                        $"Management transport '{TransportName}' has no connection string."
                    ),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ClientProvidedName = $"ratatoskr-management-{TransportName}",
            };

            var created = await factory.CreateConnectionAsync(cancellationToken);
            created.ConnectionShutdownAsync += OnShutdownAsync;
            _connection = created;
            return created;
        }
        catch (BrokerUnreachableException ex)
        {
            throw new InvalidOperationException(
                $"Management transport '{TransportName}' could not reach its broker.",
                ex
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnShutdownAsync(object sender, ShutdownEventArgs args)
    {
        // Exclusive reply and discovery queues are gone the moment the connection drops, so any
        // request still waiting for a reply can never be answered. Telling the caller now beats
        // letting it wait out a deadline against a queue that no longer exists.
        ConnectionLost?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            _connection.ConnectionShutdownAsync -= OnShutdownAsync;
            await _connection.DisposeAsync();
            _connection = null;
        }

        _gate.Dispose();
    }
}
