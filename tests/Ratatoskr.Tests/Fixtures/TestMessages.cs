using System.ComponentModel.DataAnnotations;
using Ratatoskr.Core;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>
/// Test event with CloudEvent attribute
/// </summary>
[RatatoskrMessage("test.event")]
public record TestEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Data { get; init; } = string.Empty;
}

/// <summary>
/// Rich order event with data annotations for schema generation tests.
/// Used across unit, integration, and AsyncApi tests.
/// </summary>
[RatatoskrMessage("order.created")]
public record OrderCreatedEvent
{
    [Required]
    public Guid OrderId { get; init; }

    [Required]
    [Range(0.01, 999999.99)]
    public decimal Amount { get; init; }

    [EmailAddress]
    public string? CustomerEmail { get; init; }

    [Url]
    public string? CallbackUrl { get; init; }

    [StringLength(500, MinimumLength = 1)]
    public string? Notes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Test entity for database operations
/// </summary>
public class TestEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

// Test handler implementations for message consuming tests
public class TestEventHandler : IMessageHandler<TestEvent>
{
    public List<TestEvent> HandledMessages { get; } = new();

    public Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        HandledMessages.Add(message);
        return Task.CompletedTask;
    }
}

public class SecondTestEventHandler : IMessageHandler<TestEvent>
{
    public List<TestEvent> HandledMessages { get; } = new();

    public Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        HandledMessages.Add(message);
        return Task.CompletedTask;
    }
}

public class ThrowingTestEventHandler : IMessageHandler<TestEvent>
{
    public List<TestEvent> ReceivedMessages { get; } = new();

    public Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        ReceivedMessages.Add(message);
        throw new InvalidOperationException("Handler failed intentionally");
    }
}

/// <summary>
/// No-op handler that always succeeds — used in inbox, tracking, and OTel tests.
/// </summary>
public class NoOpTestEventHandler : IMessageHandler<TestEvent>
{
    public Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;
}

/// <summary>
/// Scoped service for testing DI scope isolation.
/// Each scope gets a unique Id.
/// </summary>
public class ScopedService
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// Collects scoped service IDs across dispatches for assertion.
/// Register as singleton so it's shared across scopes.
/// </summary>
public class ScopedServiceIdCollector
{
    public List<Guid> ServiceIds { get; } = [];
}

/// <summary>
/// Handler that resolves ScopedService from DI to verify scope isolation.
/// Each dispatch should get a different ScopedService instance.
/// </summary>
public class ScopedServiceTestHandler(
    ScopedService scopedService,
    ScopedServiceIdCollector collector
) : IMessageHandler<TestEvent>
{
    public Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        collector.ServiceIds.Add(scopedService.Id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Handler that respects cancellation tokens for testing cancellation behavior.
/// </summary>
public class CancellationAwareTestHandler : IMessageHandler<TestEvent>
{
    public Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Handler that captures the MessageProperties context for assertion.
/// </summary>
public class ContextCapturingHandler : IMessageHandler<TestEvent>
{
    public MessageProperties? CapturedContext { get; private set; }

    public Task HandleAsync(
        TestEvent message,
        MessageProperties context,
        CancellationToken cancellationToken
    )
    {
        CapturedContext = context;
        return Task.CompletedTask;
    }
}
