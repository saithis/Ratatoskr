using Ratatoskr.Management.Contracts;

namespace Ratatoskr.Tests.Fixtures;

/// <summary>What the echo operation should do this time.</summary>
public sealed record EchoRequest
{
    /// <summary>Text the operation returns unchanged, so a caller can prove correlation.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>How long to take, for exercising deadlines and concurrency.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Whether to throw, for exercising exception containment.</summary>
    public bool Throw { get; init; }
}

/// <summary>What the echo operation returned.</summary>
public sealed record EchoResponse(string Message, Guid OperationId, int Invocations);

/// <summary>
/// A management operation with no storage behind it, so a transport can be tested for what it
/// actually does — routing, correlation, deadlines, failure shape — without a database in the way.
/// </summary>
public sealed class EchoManagementOperation : IManagementOperation
{
    /// <summary>The operation name this is registered under.</summary>
    public const string OperationName = "test.echo";

    private int _invocations;

    /// <summary>How many times this instance has been asked to run.</summary>
    public int Invocations => Volatile.Read(ref _invocations);

    /// <inheritdoc />
    public string Name => OperationName;

    /// <inheritdoc />
    public Type RequestType => typeof(EchoRequest);

    /// <inheritdoc />
    public async Task<ManagementResult> ExecuteAsync(
        ManagementOperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.RequestAs<EchoRequest>();
        var invocation = Interlocked.Increment(ref _invocations);

        if (request.Delay > TimeSpan.Zero)
        {
            await Task.Delay(request.Delay, cancellationToken);
        }

        if (request.Throw)
        {
            throw new InvalidOperationException(
                "Simulated failure carrying a connection string: Host=secret;Password=hunter2"
            );
        }

        return ManagementResult.Ok(
            new EchoResponse(request.Message, context.OperationId, invocation)
        );
    }
}
