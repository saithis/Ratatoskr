using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using TUnit.Core.Interfaces;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Shared RabbitMQ container for all tests in the session.
/// Starts once and reused across all tests for performance.
/// </summary>
public class RabbitMqContainerFixture : IAsyncInitializer, IAsyncDisposable
{
    private RabbitMqContainer? _container;

    public string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("Container not initialized");

    public async Task InitializeAsync()
    {
        _container = new RabbitMqBuilder("rabbitmq:4.0-alpine").Build();

        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}
