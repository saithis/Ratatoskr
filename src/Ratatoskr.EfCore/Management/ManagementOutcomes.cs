namespace Ratatoskr.EfCore.Management;

internal enum SingleRequeueOutcome { Success, NotFound, NotPoisoned, Conflict }

internal enum SingleDeleteOutcome { Success, NotFound, NotPoisoned, Conflict }

internal record RequeueMessageOutcome(bool Found, bool Conflict, IReadOnlyList<Guid> RequeuedIds);
