using AwesomeAssertions;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Testing;

public class HandlerTestInvokerTests
{
    [Test]
    public async Task InvokeAsync_CallsHandler()
    {
        // Arrange
        var handler = new TestEventHandler();
        var message = new TestEvent { Id = "test-1", Data = "handler test" };

        // Act
        await HandlerTestInvoker.InvokeAsync(handler, message);

        // Assert
        handler.HandledMessages.Should().ContainSingle()
            .Which.Id.Should().Be("test-1");
    }

    [Test]
    public async Task InvokeAsync_WithProperties_PassesPropertiesToHandler()
    {
        // Arrange
        var handler = new ContextCapturingHandler();
        var message = new TestEvent { Id = "props-test", Data = "data" };
        var properties = new MessageProperties
        {
            Subject = "test-subject",
            Source = "/test"
        };

        // Act
        await HandlerTestInvoker.InvokeAsync(handler, message, properties);

        // Assert
        handler.CapturedContext.Should().NotBeNull();
        handler.CapturedContext!.Subject.Should().Be("test-subject");
        handler.CapturedContext.Source.Should().Be("/test");
    }

    [Test]
    public async Task InvokeAsync_WithoutProperties_CreatesDefault()
    {
        // Arrange
        var handler = new ContextCapturingHandler();
        var message = new TestEvent { Data = "no-props" };

        // Act
        await HandlerTestInvoker.InvokeAsync(handler, message);

        // Assert
        handler.CapturedContext.Should().NotBeNull();
    }

    [Test]
    public async Task InvokeAsync_WithCancellationToken_PassesToken()
    {
        // Arrange
        var handler = new CancellationAwareTestHandler();
        var message = new TestEvent { Data = "cancel" };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        var act = () => HandlerTestInvoker.InvokeAsync(handler, message, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
