namespace PlaygroundHost.Infrastructure;

public enum OutboxSendFailureKind
{
    Succeed = 0,
    AlwaysFail = 1,
    SucceedAfterNFailures = 2,
}
