# Ratatoskr.Management.Abstractions

Dependency-free, versioned contracts for Ratatoskr management providers.

Commands are delivered **at least once**. Mutating commands must use a stable `OperationId` and an idempotency guard appropriate to the resource. A caller that loses a response may retry with the same operation ID.

`ManagementTarget.LogicalServiceName` targets one eligible replica. Supplying `InstanceId` targets that replica only. `ResourceId` identifies the provider-neutral resource operated on by the command.

Protocol compatibility is major-version based: a receiver accepts the same major version and a minor version no greater than its supported minor version. Unsupported protocol versions, operations, and capabilities use the stable error codes in `ManagementProtocol`.

Providers implement `IManagementClient`, `IServiceCatalog`, command hosting, and discovery
interfaces without exposing their broker topology. Shared list operations use opaque keyset
cursors (`CursorPageRequest` / `CursorPagedResult<T>`), so callers do not rely on page offsets.
