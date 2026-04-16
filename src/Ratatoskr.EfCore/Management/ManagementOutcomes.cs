namespace Ratatoskr.EfCore.Management;

internal enum SingleRequeueOutcome { Success, NotFound, NotPoisoned, Conflict }
