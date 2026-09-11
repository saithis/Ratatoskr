using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ratatoskr.Management.Agent;
using Ratatoskr.Management.Contracts;
using TUnit.Core;

namespace Ratatoskr.Tests.Management;

public sealed class ManagementOperationDispatcherTests
{
    [Test]
    public async Task DispatchAsync_RegisteredOperation_DelegatesToFeatureHandler()
    {
        var featureHandler = new StubOperationHandler("feature.operation");
        var dispatcher = new ManagementOperationDispatcher(
            [featureHandler],
            NullLogger<ManagementOperationDispatcher>.Instance
        );

        var response = await dispatcher.DispatchAsync(CreateRequest("feature.operation"));

        featureHandler.WasCalled.Should().BeTrue();
        response.Status.Should().Be(ManagementResponseStatus.Succeeded);
        response.Payload?.Json.Should().Be("feature-result");
    }

    [Test]
    public async Task DispatchAsync_UnregisteredOperation_ReturnsStableError()
    {
        var dispatcher = new ManagementOperationDispatcher(
            [],
            NullLogger<ManagementOperationDispatcher>.Instance
        );

        var response = await dispatcher.DispatchAsync(CreateRequest("not.registered"));

        response.Status.Should().Be(ManagementResponseStatus.Failed);
        response.Error.Should().Be(new ManagementError(
            ManagementProtocol.UnsupportedOperation,
            "The operation is not supported."
        ));
    }

    private static ManagementRequestEnvelope CreateRequest(string operation) => new()
    {
        ProtocolVersion = ManagementProtocol.Current,
        RequestId = "request-001",
        OperationId = "operation-001",
        Target = new ManagementTarget("orders"),
        Operation = operation,
        Deadline = DateTimeOffset.UtcNow,
    };

    private sealed class StubOperationHandler(string operation) : IManagementOperationHandler
    {
        public string Operation { get; } = operation;
        public bool WasCalled { get; private set; }

        public Task<ManagementResponseEnvelope> HandleAsync(
            ManagementRequestEnvelope request,
            CancellationToken cancellationToken = default
        )
        {
            WasCalled = true;
            return Task.FromResult(ManagementResponseEnvelope.Ok(
                request,
                new ManagementPayload("application/json", "feature-result")
            ));
        }
    }
}
