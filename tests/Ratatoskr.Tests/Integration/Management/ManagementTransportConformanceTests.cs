using AwesomeAssertions;
using Ratatoskr.Management.Contracts;
using Ratatoskr.Management.Registry;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

/// <summary>
/// What a control-plane transport is asked to do, written once and run against every transport.
/// </summary>
/// <remarks>
/// This is the thing that makes "a second transport can be added without changing shared
/// interfaces" a claim rather than a hope: a new transport implements
/// <see cref="IManagementTransport"/> and <see cref="IManagementDiscoverySource"/>, subclasses
/// this, and either passes or does not.
/// </remarks>
public abstract class ManagementTransportConformanceTests
{
    /// <summary>The name the transport under test is registered under.</summary>
    protected abstract string TransportName { get; }

    /// <summary>Starts one agent and one dashboard connected by the transport under test.</summary>
    protected abstract Task<ConformanceHost> StartAsync();

    [Test]
    public async Task Discovery_ExposesTheServiceAndItsReplica()
    {
        await using var host = await StartAsync();

        var detail = await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        detail.TransportName.Should().Be(TransportName);
        detail.ServiceName.Should().Be(host.ServiceName);
        detail.Liveness.Should().Be(ServiceLiveness.Online);
        detail.Instances.Should().ContainSingle(i => i.InstanceId == host.InstanceId);
    }

    [Test]
    public async Task LogicalTargeting_ReachesTheService()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var response = await EchoAsync(host, "hello");

        response.Message.Should().Be("hello");
    }

    [Test]
    public async Task InstanceTargeting_ReachesTheNamedReplica()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var response = await host.Client.ExecuteAsync<EchoResponse>(
            TransportName,
            host.ServiceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "addressed" },
            instanceId: host.InstanceId
        );

        response.Message.Should().Be("addressed");
    }

    [Test]
    public async Task InstanceTargeting_AtAReplicaThatDoesNotExist_FailsFast()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var started = DateTimeOffset.UtcNow;
        var response = await host.Client.SendAsync(
            TransportName,
            host.ServiceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = "nobody home" },
            instanceId: "no-such-replica",
            timeout: TimeSpan.FromSeconds(30)
        );

        response.IsSuccess.Should().BeFalse();
        response.Error!.Code.Should().Be(ManagementErrorCodes.TargetUnreachable);
        (DateTimeOffset.UtcNow - started)
            .Should()
            .BeLessThan(TimeSpan.FromSeconds(15), "an unreachable replica must not be waited out");
    }

    [Test]
    public async Task UnknownService_FailsWithoutWaitingOutTheDeadline()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var response = await host.Client.SendAsync(
            TransportName,
            "a-service-that-was-never-announced",
            EchoManagementOperation.OperationName,
            new EchoRequest(),
            timeout: TimeSpan.FromSeconds(30)
        );

        response.IsSuccess.Should().BeFalse();
        response.Error!.Code.Should().Be(ManagementErrorCodes.TargetUnreachable);
    }

    [Test]
    public async Task UnsupportedOperation_ComesBackAsAValueNotAnException()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var response = await host.Client.SendAsync(
            TransportName,
            host.ServiceName,
            "test.no-such-operation",
            new EchoRequest()
        );

        response.IsSuccess.Should().BeFalse();
        response.Status.Should().Be(ManagementResultStatus.Unsupported);
        response.Error!.Code.Should().Be(ManagementErrorCodes.UnsupportedOperation);
    }

    [Test]
    public async Task Deadline_IsEnforcedByTheCaller()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var response = await host.Client.SendAsync(
            TransportName,
            host.ServiceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Delay = TimeSpan.FromSeconds(10) },
            timeout: TimeSpan.FromSeconds(2)
        );

        response.IsSuccess.Should().BeFalse();
        response.Error!.Code.Should().Be(ManagementErrorCodes.DeadlineExceeded);
        response.Error.IsRetryable.Should().BeTrue();
    }

    [Test]
    public async Task ConcurrentRequests_AreCorrelatedIndependently()
    {
        // The failure this guards against is the quiet one: a correlation bug delivers answers to
        // the wrong callers, and every request still "succeeds".
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var messages = Enumerable.Range(0, 16).Select(index => $"message-{index}").ToArray();

        var responses = await Task.WhenAll(messages.Select(message => EchoAsync(host, message)));

        responses.Select(response => response.Message).Should().BeEquivalentTo(messages);
    }

    [Test]
    public async Task OperationFailure_IsReportedWithoutLeakingExceptionText()
    {
        await using var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);

        var response = await host.Client.SendAsync(
            TransportName,
            host.ServiceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Throw = true }
        );

        response.IsSuccess.Should().BeFalse();
        response.Error!.Code.Should().Be(ManagementErrorCodes.InternalError);
        response
            .Error.Detail.Should()
            .NotContain("hunter2", "raw exception text must never cross the protocol");
        response.Error.Detail.Should().NotContain("Host=secret");
    }

    [Test]
    public async Task Shutdown_CompletesWithoutAbandonedWork()
    {
        var host = await StartAsync();
        await host.Client.WaitForServiceAsync(TransportName, host.ServiceName);
        await EchoAsync(host, "before shutdown");

        var stop = host.DisposeAsync().AsTask();

        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        stop.IsCompletedSuccessfully.Should().BeTrue();
    }

    private Task<EchoResponse> EchoAsync(ConformanceHost host, string message) =>
        host.Client.ExecuteAsync<EchoResponse>(
            TransportName,
            host.ServiceName,
            EchoManagementOperation.OperationName,
            new EchoRequest { Message = message }
        );
}

/// <summary>One running agent plus one running dashboard, connected by a transport.</summary>
public sealed class ConformanceHost(
    ManagementTestClient client,
    string serviceName,
    string instanceId,
    Func<ValueTask> disposeAsync
) : IAsyncDisposable
{
    private bool _disposed;

    /// <summary>The dashboard's view of the control plane.</summary>
    public ManagementTestClient Client { get; } = client;

    /// <summary>The service name under test.</summary>
    public string ServiceName { get; } = serviceName;

    /// <summary>The instance ID under test.</summary>
    public string InstanceId { get; } = instanceId;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await disposeAsync();
    }
}
