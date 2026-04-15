namespace Ratatoskr.EfCore.Management.Dto;

// body: { "ids": ["..."] }  OR  { "all": true }
internal record BulkActionRequest(List<Guid>? Ids, bool? All);

internal record BulkActionResult(List<Guid> Succeeded, List<BulkFailure> Failed);
internal record BulkFailure(Guid Id, string Reason);
