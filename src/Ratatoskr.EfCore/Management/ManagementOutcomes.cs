namespace Ratatoskr.EfCore.Management;

internal enum SingleRequeueOutcome { Success, NotFound, NotPoisoned, Conflict }

internal enum SingleDeleteOutcome { Success, NotFound, NotPoisoned, Conflict }
