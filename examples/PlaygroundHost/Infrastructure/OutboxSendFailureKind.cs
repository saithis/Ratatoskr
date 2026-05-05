namespace PlaygroundHost.Infrastructure;

public enum OutboxSendFailureKind
{
    Succeed,
    AlwaysFail,
    SucceedAfterNFailures,
}
